using System.Text.Json;
using monitor_services_api.Models;

namespace monitor_services_api.Services
{
    /// <summary>
    /// Serviço responsável por calcular o downtime dos serviços monitorados,
    /// baseado nos eventos do Zabbix nos últimos 30 dias.
    /// Lógica adaptada do script Python calculaDowntime.py
    /// </summary>
    public class DowntimeCalculationService
    {
        private readonly ZabbixService _zabbix;
        private readonly ClientConfigService _clientConfig;
        private readonly ILogger<DowntimeCalculationService> _logger;

        // Novo: caminho do arquivo TXT de downtime
        private string GetDowntimeTxtPath(string clientId)
        {
            var clientFolder = Path.Combine(Directory.GetCurrentDirectory(), "clientes", clientId);
            Directory.CreateDirectory(clientFolder);
            return Path.Combine(clientFolder, "downtime.txt");
        }

        public DowntimeCalculationService(
            IZabbixService zabbix, 
            ClientConfigService clientConfig,
            ILogger<DowntimeCalculationService> logger)
        {
            _zabbix = (ZabbixService)zabbix;
            _clientConfig = clientConfig;
            _logger = logger;
        }

        /// <summary>
        /// Calcula o downtime de todos os serviços de um cliente nos últimos 30 dias
        /// </summary>
        public async Task<DowntimeReportResponse> CalculateClientDowntimeAsync(string clientId, int days = 30)
        {
            _logger.LogInformation($"Calculando downtime para cliente '{clientId}' - últimos {days} dias");
            
            if (!_clientConfig.ClientExists(clientId))
            {
                _logger.LogWarning($"Cliente '{clientId}' não encontrado");
                return null;
            }

            _zabbix.SetCurrentClient(clientId);
            
            var services = _zabbix.GetMonitoredServices().ToList();
            if (!services.Any())
            {
                _logger.LogWarning($"Nenhum serviço configurado para o cliente '{clientId}'");
                return new DowntimeReportResponse 
                { 
                    ClientId = clientId, 
                    PeriodDays = days 
                };
            }

            var report = new DowntimeReportResponse
            {
                ClientId = clientId,
                PeriodDays = days,
                GeneratedAt = DateTime.Now,
                Services = new List<ServiceDowntimeDetail>()
            };

            long totalDowntimeSeconds = 0;

            foreach (var serviceName in services)
            {
                var serviceIp = _zabbix.GetServiceIp(serviceName);
                _logger.LogInformation($"Verificando downtime de: {serviceName} ({serviceIp})");

                var (downtimeSeconds, incidents) = await GetDowntimeForServiceAsync(serviceName, days);
                totalDowntimeSeconds += downtimeSeconds;

                // Novo: Atualiza histórico TXT
                await UpdateDowntimeTxtAsync(clientId, serviceName, incidents);

                report.Services.Add(new ServiceDowntimeDetail
                {
                    ServiceName = serviceName,
                    IpAddress = serviceIp ?? "N/A",
                    TotalDowntimeSeconds = downtimeSeconds,
                    TotalDowntimeFormatted = FormatDuration(downtimeSeconds),
                    IncidentCount = incidents.Count,
                    Incidents = incidents
                });
            }

            report.TotalDowntimeSeconds = totalDowntimeSeconds;
            report.TotalDowntimeFormatted = FormatDuration(totalDowntimeSeconds);
            report.ServicesCount = services.Count;
            report.ServicesWithDowntime = report.Services.Count(s => s.TotalDowntimeSeconds > 0);

            // Salvar relatório em arquivo
            await SaveDowntimeReportAsync(clientId, report);

            _logger.LogInformation($"Cálculo concluído. Total: {report.TotalDowntimeFormatted}");
            
            return report;
        }

        /// <summary>
        /// Busca eventos de trigger que contenham o nome do serviço e "is not running"
        /// Lógica adaptada da função get_downtime_for_service() do Python
        /// </summary>
        private async Task<(long TotalSeconds, List<IncidentDetail> Incidents)> GetDowntimeForServiceAsync(
            string serviceName, 
            int days)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var periodStart = now - (days * 24 * 60 * 60);

            try
            {
                // Busca eventos de problema (value=1) que contenham o nome do serviço E "is not running"
                var events = await _zabbix.RequestAsync<List<ZabbixEvent>>("event.get", new
                {
                    output = new[] { "eventid", "clock", "r_eventid", "name" },
                    search = new
                    {
                        name = new[] { serviceName, "is not running" }
                    },
                    searchByAny = false, // Garante AND (ambos os termos devem estar presentes)
                    time_from = periodStart,
                    time_till = now,
                    value = 1, // Apenas eventos de PROBLEMA
                    sortfield = new[] { "clock" },
                    sortorder = "ASC"
                });

                if (events == null || !events.Any())
                {
                    _logger.LogDebug($"Nenhum incidente 'is not running' encontrado para {serviceName}");
                    return (0, new List<IncidentDetail>());
                }

                _logger.LogInformation($"Encontrados {events.Count} eventos para {serviceName}");

                // Coletar IDs de recuperação válidos
                var recoveryIds = events
                    .Where(e => e.R_eventid != "0" && !string.IsNullOrEmpty(e.R_eventid))
                    .Select(e => e.R_eventid)
                    .ToList();

                // Mapa de recuperação: eventId -> timestamp
                var recoveryMap = new Dictionary<string, long>();

                if (recoveryIds.Any())
                {
                    var recoveryEvents = await _zabbix.RequestAsync<List<ZabbixEvent>>("event.get", new
                    {
                        output = new[] { "clock", "eventid" },
                        eventids = recoveryIds
                    });

                    if (recoveryEvents != null)
                    {
                        recoveryMap = recoveryEvents.ToDictionary(
                            e => e.Eventid, 
                            e => long.Parse(e.Clock)
                        );
                    }
                }

                long totalSeconds = 0;
                var incidents = new List<IncidentDetail>();


                foreach (var evt in events)
                {
                    var startTime = long.Parse(evt.Clock);
                    long endTime;
                    bool isActive = false;

                    // LÓGICA DE INCIDENTE ATIVO:
                    // Se não houver recovery_id OU se o ID não retornou data, 
                    // o downtime conta até "agora"
                    if (evt.R_eventid != "0" && recoveryMap.TryGetValue(evt.R_eventid, out var recoveryTime))
                    {
                        endTime = recoveryTime;
                    }
                    else
                    {
                        endTime = now;
                        isActive = true;
                    }

                    var duration = endTime - startTime;
                    totalSeconds += duration;

                    incidents.Add(new IncidentDetail
                    {
                        StartTime = DateTimeOffset.FromUnixTimeSeconds(startTime).LocalDateTime,
                        EndTime = isActive ? null : DateTimeOffset.FromUnixTimeSeconds(endTime).LocalDateTime,
                        DurationSeconds = duration,
                        DurationFormatted = FormatDuration(duration),
                        TriggerName = evt.Name,
                        IsActive = isActive
                    });
                }

                return (totalSeconds, incidents);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erro ao calcular downtime para {serviceName}");
                return (0, new List<IncidentDetail>());
            }
        }

        /// <summary>
        /// Converte segundos em formato legível (ex: 2h 30m)
        /// </summary>
        private string FormatDuration(long seconds)
        {
            if (seconds == 0) return "0m";
            
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            
            if (hours > 0)
                return $"{hours}h {minutes}m";
            
            return $"{minutes}m";
        }

        /// <summary>
        /// Atualiza o arquivo downtime.txt do cliente com incidentes abertos e resolvidos
        /// </summary>
        private async Task UpdateDowntimeTxtAsync(string clientId, string serviceName, List<IncidentDetail> incidents)
        {
            var txtPath = GetDowntimeTxtPath(clientId);
            var lines = new List<string>();
            if (File.Exists(txtPath))
            {
                lines = (await File.ReadAllLinesAsync(txtPath)).ToList();
            }

            // Remove linhas antigas deste serviço
            lines.RemoveAll(l => l.Contains($"Serviço: {serviceName} "));

            foreach (var inc in incidents)
            {
                if (inc.IsActive)
                {
                    lines.Add($"[ABERTO] Serviço: {serviceName} | Início: {inc.StartTime:yyyy-MM-dd HH:mm:ss} | UnixStart: {((DateTimeOffset)inc.StartTime).ToUnixTimeSeconds()}");
                }
                else
                {
                    lines.Add($"[RESOLVIDO] Serviço: {serviceName} | Início: {inc.StartTime:yyyy-MM-dd HH:mm:ss} | Fim: {inc.EndTime:yyyy-MM-dd HH:mm:ss} | Duração: {inc.DurationFormatted} | Trigger: {inc.TriggerName}");
                }
            }

            await File.WriteAllLinesAsync(txtPath, lines);
        }

        /// <summary>
        /// Salva o relatório de downtime em arquivo JSON na pasta do cliente
        /// </summary>
        private async Task SaveDowntimeReportAsync(string clientId, DowntimeReportResponse report)
        {
            try
            {
                var clientFolder = Path.Combine(Directory.GetCurrentDirectory(), "clientes", clientId);
                Directory.CreateDirectory(clientFolder);

                var filePath = Path.Combine(clientFolder, "downtime_report.json");
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                await File.WriteAllTextAsync(filePath, json);
                
                _logger.LogInformation($"Relatório salvo em: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar relatório de downtime");
            }
        }

        /// <summary>
        /// Carrega o relatório de downtime salvo anteriormente
        /// </summary>
        public async Task<DowntimeReportResponse?> GetSavedReportAsync(string clientId)
        {
            try
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "clientes", clientId, "downtime_report.json");
                
                if (!File.Exists(filePath))
                {
                    _logger.LogDebug($"Nenhum relatório salvo encontrado para {clientId}");
                    return null;
                }

                var json = await File.ReadAllTextAsync(filePath);
                var report = JsonSerializer.Deserialize<DowntimeReportResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erro ao carregar relatório salvo de {clientId}");
                return null;
            }
        }
    }
}
