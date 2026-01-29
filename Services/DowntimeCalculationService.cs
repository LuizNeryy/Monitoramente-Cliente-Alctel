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

        // Locks para evitar race condition na escrita de arquivos
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

        // Novo: caminho do arquivo TXT de downtime
        private string GetDowntimeTxtPath(string clientId)
        {
            var clientFolder = Path.Combine(AppContext.BaseDirectory, "clientes", clientId);
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
            
            // Limpa arquivos temporários órfãos na inicialização
            CleanupOrphanedTempFiles();
        }

        /// <summary>
        /// Remove arquivos .tmp órfãos que ficaram de escritas anteriores interrompidas
        /// </summary>
        private void CleanupOrphanedTempFiles()
        {
            try
            {
                var clientsPath = Path.Combine(AppContext.BaseDirectory, "clientes");
                if (!Directory.Exists(clientsPath)) return;

                var tempFiles = Directory.GetFiles(clientsPath, "*.tmp", SearchOption.AllDirectories);
                foreach (var tempFile in tempFiles)
                {
                    try
                    {
                        File.Delete(tempFile);
                        _logger.LogDebug($"Arquivo temporário órfão removido: {tempFile}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Não foi possível remover arquivo temporário: {tempFile}");
                    }
                }
                
                if (tempFiles.Length > 0)
                {
                    _logger.LogInformation($"Limpeza concluída: {tempFiles.Length} arquivo(s) temporário(s) órfão(s) removido(s)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao limpar arquivos temporários órfãos");
            }
        }

        /// <summary>
        /// Calcula o downtime de todos os serviços de um cliente nos últimos 30 dias
        /// </summary>
        public async Task<DowntimeReportResponse> CalculateClientDowntimeAsync(string clientId, int days = 20)
        {
            _logger.LogInformation($"Calculando downtime para cliente '{clientId}' - últimos {days} dias");
            
            if (!_clientConfig.ClientExists(clientId))
            {
                _logger.LogWarning($"Cliente '{clientId}' não encontrado");
                return null!;
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
            long totalDowntimeMinutesRounded = 0; // Soma dos minutos arredondados (para bater visualmente)
            int servicesNotFoundInZabbix = 0;

            foreach (var serviceName in services)
            {
                var serviceIp = _zabbix.GetServiceIp(serviceName);
                _logger.LogDebug($"Verificando downtime de: {serviceName} ({serviceIp})");

                // TODO: Adicionar verificação se o serviço existe no Zabbix
                // Por enquanto, assume que existe se está no txt

                var (downtimeSeconds, incidents) = await GetDowntimeForServiceAsync(serviceName, days);
                totalDowntimeSeconds += downtimeSeconds;

                // Converte para minutos arredondando pra cima (se tiver qualquer segundo, conta 1 minuto)
                var downtimeMinutes = (long)Math.Ceiling(downtimeSeconds / 60.0);
                totalDowntimeMinutesRounded += downtimeMinutes;

                // Novo: Atualiza histórico TXT
                await UpdateDowntimeTxtAsync(clientId, serviceName, incidents);

                report.Services.Add(new ServiceDowntimeDetail
                {
                    ServiceName = serviceName,
                    IpAddress = serviceIp ?? "N/A",
                    TotalDowntimeSeconds = downtimeSeconds,
                    TotalDowntimeFormatted = FormatDurationRoundedUp(downtimeSeconds),
                    IncidentCount = incidents.Count,
                    Incidents = incidents
                });
            }

            // Usa a soma dos minutos arredondados para o total (bate com a soma visual)
            report.TotalDowntimeSeconds = totalDowntimeMinutesRounded * 60; // Converte de volta pra segundos
            report.TotalDowntimeFormatted = FormatDurationFromMinutes(totalDowntimeMinutesRounded);
            report.ServicesCount = report.Services.Count; // SEMPRE usa a quantidade real retornada pelo Zabbix
            report.ServicesWithDowntime = report.Services.Count(s => s.TotalDowntimeSeconds > 0);

            // Agrega incidentes ativos e resolvidos de todos os serviços
            report.ActiveIncidents = report.Services
                .SelectMany(s => s.Incidents)
                .Where(i => i.IsActive)
                .OrderByDescending(i => i.StartTime)
                .ToList();
            
            report.ResolvedIncidents = report.Services
                .SelectMany(s => s.Incidents)
                .Where(i => !i.IsActive)
                .OrderByDescending(i => i.StartTime)
                .ToList();

            // Log detalhado - mostra diferença entre TXT e Zabbix
            _logger.LogInformation($"Serviços no TXT: {services.Count} | Processados REALMENTE pelo Zabbix: {report.Services.Count} | Com downtime: {report.ServicesWithDowntime}");
            
            if (servicesNotFoundInZabbix > 0)
            {
                _logger.LogWarning($"⚠️ IGNORADOS: {servicesNotFoundInZabbix} serviço(s) listado(s) no TXT não foram encontrados no Zabbix (nome/IP errado?)");
            }

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
                // IMPORTANTE: Busca o host específico pelo IP para filtrar eventos apenas desse servidor
                var serviceIp = _zabbix.GetServiceIp(serviceName);
                List<string>? hostIds = null;
                
                if (!string.IsNullOrEmpty(serviceIp))
                {
                    var hosts = await _zabbix.GetHostsAsync(serviceIp);
                    if (hosts != null && hosts.Any())
                    {
                        hostIds = hosts.Select(h => h.Hostid).ToList();
                        _logger.LogDebug($"Filtrando eventos apenas do(s) host(s) com IP {serviceIp}: {string.Join(", ", hostIds)}");
                    }
                    else
                    {
                        _logger.LogWarning($"Nenhum host encontrado no Zabbix com IP {serviceIp} para o serviço {serviceName}");
                        return (0, new List<IncidentDetail>());
                    }
                }

                // Busca eventos de problema (value=1) que contenham o nome do serviço E "is not running"
                var requestParams = new Dictionary<string, object>
                {
                    ["output"] = new[] { "eventid", "clock", "r_eventid", "name" },
                    ["search"] = new
                    {
                        name = new[] { serviceName, "is not running" }
                    },
                    ["searchByAny"] = false, // Garante AND (ambos os termos devem estar presentes)
                    ["time_from"] = periodStart,
                    ["time_till"] = now,
                    ["value"] = 1, // Apenas eventos de PROBLEMA
                    ["sortfield"] = new[] { "clock" },
                    ["sortorder"] = "ASC"
                };

                // CORREÇÃO CRÍTICA: Adiciona filtro por hostids se disponível
                if (hostIds != null && hostIds.Any())
                {
                    requestParams["hostids"] = hostIds;
                }

                var events = await _zabbix.RequestAsync<List<ZabbixEvent>>("event.get", requestParams);

                if (events == null || !events.Any())
                {
                    _logger.LogDebug($"Nenhum incidente 'is not running' encontrado para {serviceName}");
                    return (0, new List<IncidentDetail>());
                }

                _logger.LogDebug($"Encontrados {events.Count} eventos para {serviceName}");


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
                    
                    // FILTRO CRÍTICO: Ignora incidentes que começaram ANTES do período
                    if (startTime < periodStart)
                    {
                        continue;
                    }
                    
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
        /// Formata duração arredondando PARA CIMA (1-59s = 1m)
        /// </summary>
        private string FormatDurationRoundedUp(long seconds)
        {
            if (seconds == 0) return "0m";
            
            var totalMinutes = (long)Math.Ceiling(seconds / 60.0);
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            
            if (hours > 0)
                return $"{hours}h {minutes}m";
            
            return $"{minutes}m";
        }

        /// <summary>
        /// Formata duração a partir de minutos já calculados
        /// </summary>
        private string FormatDurationFromMinutes(long totalMinutes)
        {
            if (totalMinutes == 0) return "0m";
            
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            
            if (hours > 0)
                return $"{hours}h {minutes}m";
            
            return $"{minutes}m";
        }

        /// <summary>
        /// Atualiza o arquivo downtime.txt do cliente com incidentes abertos e resolvidos
        /// Usa escrita atômica para evitar race conditions
        /// </summary>
        private async Task UpdateDowntimeTxtAsync(string clientId, string serviceName, List<IncidentDetail> incidents)
        {
            await _fileLock.WaitAsync();
            try
            {
                var txtPath = GetDowntimeTxtPath(clientId);
                var tempPath = txtPath + ".tmp";
            
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

                // Escreve em arquivo temporário primeiro
                await File.WriteAllLinesAsync(tempPath, lines);
                
                // Renomeia para o arquivo final (operação atômica)
                // Se falhar, o arquivo antigo permanece intacto
                File.Move(tempPath, txtPath, overwrite: true);
                
                _logger.LogDebug($"Arquivo {txtPath} atualizado atomicamente via {tempPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erro ao atualizar downtime.txt para {clientId}/{serviceName}");
                // Se falhou, tenta limpar o arquivo temporário
                var tempPath = GetDowntimeTxtPath(clientId) + ".tmp";
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                throw;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Salva o relatório de downtime em arquivo JSON na pasta do cliente
        /// Usa escrita atômica (escreve em .tmp e depois renomeia) para evitar race conditions
        /// O front-end NUNCA vai ler dados pela metade!
        /// </summary>
        private async Task SaveDowntimeReportAsync(string clientId, DowntimeReportResponse report)
        {
            await _fileLock.WaitAsync();
            try
            {
                var clientFolder = Path.Combine(AppContext.BaseDirectory, "clientes", clientId);
                Directory.CreateDirectory(clientFolder);

                var filePath = Path.Combine(clientFolder, "downtime_report.json");
                var tempFilePath = Path.Combine(clientFolder, "downtime_report.json.tmp");
                
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                // PASSO 1: Escreve TUDO em arquivo temporário (frontend não vê)
                await File.WriteAllTextAsync(tempFilePath, json);
                
                // PASSO 2: Renomeia atomicamente (instantâneo - tudo ou nada)
                // Se der erro aqui, o arquivo antigo permanece intacto no frontend
                File.Move(tempFilePath, filePath, overwrite: true);
                
                _logger.LogDebug($"Relatório de {clientId} salvo atomicamente ({json.Length} bytes)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar relatório de downtime");
                
                // Limpa arquivo temporário órfão se existir
                var tempFilePath = Path.Combine(AppContext.BaseDirectory, "clientes", clientId, "downtime_report.json.tmp");
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
                throw;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Carrega o relatório de downtime salvo anteriormente
        /// </summary>
        public async Task<DowntimeReportResponse?> GetSavedReportAsync(string clientId)
        {
            try
            {
                var filePath = Path.Combine(AppContext.BaseDirectory, "clientes", clientId, "downtime_report.json");
                
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
