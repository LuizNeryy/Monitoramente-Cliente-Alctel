using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using monitor_services_api.Models;
using monitor_services_api.Services;

namespace monitor_services_api.Controllers
{
    [ApiController]
    [Route("api")]
    public class MonitorController : ControllerBase
    {
        private readonly ZabbixService _zabbix;
        private readonly ClientConfigService _clientConfig;
        private readonly DowntimeHistoryService _downtimeHistory;
        private readonly DowntimeCalculationService _downtimeCalculation;

        private static readonly Dictionary<string, string> PriorityMap = new()
        {
            ["0"] = "Não classificada",
            ["1"] = "Informação",
            ["2"] = "Atenção",
            ["3"] = "Média",
            ["4"] = "Alta",
            ["5"] = "Crítica"
        };

        private static readonly Dictionary<int, (string Status, bool Active)> ServiceStatusMap = new()
        {
            [0] = ("Running", true),
            [1] = ("Paused", false),
            [2] = ("Starting", true),
            [3] = ("Pausing", false),
            [4] = ("Resuming", true),
            [5] = ("Stopping", false),
            [6] = ("Stopped", false),
            [7] = ("Unknown", false),
            [255] = ("Not Found", false)
        };

        public MonitorController(IZabbixService zabbix, ClientConfigService clientConfig, DowntimeHistoryService downtimeHistory, DowntimeCalculationService downtimeCalculation)
        {
            _zabbix = (ZabbixService)zabbix;
            _clientConfig = clientConfig;
            _downtimeHistory = downtimeHistory;
            _downtimeCalculation = downtimeCalculation;
        }

        private bool ConfigureClientContext(string? clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return false;

            if (!_clientConfig.ClientExists(clientId))
                return false;

            _zabbix.SetCurrentClient(clientId);
            return true;
        }

        private static long ToUnixTime(DateTime dt) => ((DateTimeOffset)dt).ToUnixTimestamp();
        private static DateTime FromUnixTime(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;

        [HttpGet("{clientId}/host-info")]
        public async Task<IActionResult> GetHostInfoByClient(string clientId)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            var uniqueIps = _zabbix.GetUniqueHostIps().ToList();
            if (!uniqueIps.Any()) return NotFound(new { error = "Nenhum host configurado" });

            // Pega info do primeiro host disponível como exemplo
            var hosts = await _zabbix.GetHostsAsync(uniqueIps[0]);
            if (!hosts.Any()) return NotFound(new { error = "Host não encontrado" });

            var host = hosts[0];
            var iface = host.Interfaces.FirstOrDefault();

            return Ok(new HostInfoResponse
            {
                Hostid = host.Hostid,
                Hostname = host.Name,
                Ip = iface?.Ip ?? "N/A",
                Status = iface?.Available == "1" ? "Online" : "Offline",
                Available = iface?.Available ?? "",
                Error = iface?.Error ?? ""
            });
        }

        [HttpGet("{clientId}/services")]
        public async Task<IActionResult> GetServicesByClient(string clientId)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            // LÊ DO ARQUIVO SALVO (super rápido, sem consultar Zabbix)
            try
            {
                var report = await _downtimeCalculation.GetSavedReportAsync(clientId);
                
                if (report != null && report.Services != null)
                {
                    // Retorna status dos serviços baseado no relatório salvo
                    var services = _zabbix.GetMonitoredServices()
                        .Select(serviceName =>
                        {
                            // Procura o serviço no relatório para ver se tem incidentes ativos
                            var serviceReport = report.Services
                                .FirstOrDefault(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
                            
                            var hasActiveIncident = serviceReport?.Incidents?.Any(i => i.IsActive) ?? false;
                            
                            return new ServiceResponse
                            {
                                Name = serviceName,
                                Status = hasActiveIncident ? "Stopped" : "Running",
                                Active = !hasActiveIncident,
                                Lastcheck = report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss")
                            };
                        })
                        .OrderBy(s => s.Name)
                        .ToList();

                    // Cache por 60 segundos (sincronizado com BackgroundService)
                    var etag = $"\"{report.GeneratedAt.Ticks}\"";
                    Response.Headers.Append("Cache-Control", "public, max-age=60");
                    Response.Headers.Append("Last-Modified", report.GeneratedAt.ToUniversalTime().ToString("R"));
                    Response.Headers.Append("ETag", etag);
                    
                    // Se cliente já tem a versão mais recente, retorna 304
                    if (Request.Headers.ContainsKey("If-None-Match") && Request.Headers["If-None-Match"] == etag)
                    {
                        return StatusCode(304);
                    }
                    
                    return Ok(services);
                }
            }
            catch (Exception ex)
            {
                // Erro ao ler relatório - usa fallback
            }

            // FALLBACK: Se não tiver arquivo, retorna lista básica do servicos.txt
            var basicServices = _zabbix.GetMonitoredServices()
                .Select(name => new ServiceResponse
                {
                    Name = name,
                    Status = "Unknown",
                    Active = true,
                    Lastcheck = "Aguardando dados..."
                })
                .OrderBy(s => s.Name)
                .ToList();

            return Ok(basicServices);
        }

        [HttpGet("{clientId}/problems")]
        public async Task<IActionResult> GetProblemsByClient(string clientId, [FromQuery] int? resolved = null, [FromQuery] long? from = null)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            // ===== LÊ DO CACHE (JSON) - ZERO CHAMADAS AO ZABBIX =====
            var report = await _downtimeCalculation.GetSavedReportAsync(clientId);
            
            if (report == null)
            {
                return StatusCode(503, new { error = "Dados ainda não disponíveis. Aguarde o próximo ciclo de atualização." });
            }

            var allProblems = new List<ProblemResponse>();

            // Converte incidentes do relatório em ProblemResponse
            foreach (var service in report.Services)
            {
                if (service.Incidents == null || !service.Incidents.Any())
                    continue;

                foreach (var incident in service.Incidents)
                {
                    // Filtro: resolved=1 retorna apenas resolvidos, resolved=0/null retorna apenas ativos
                    if (resolved == 1 && incident.IsActive) continue;
                    if (resolved == 0 && !incident.IsActive) continue;

                    // Determina severidade baseada no nome do trigger
                    var severity = "Média";
                    var severityLevel = "3";
                    
                    if (incident.TriggerName.Contains("not running", StringComparison.OrdinalIgnoreCase))
                    {
                        severity = "Alta";
                        severityLevel = "4";
                    }

                    allProblems.Add(new ProblemResponse
                    {
                        Name = incident.TriggerName,
                        Severity = severity,
                        SeverityLevel = severityLevel,
                        Status = incident.IsActive ? "Ativo" : "Resolvido",
                        Started = incident.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        DurationMinutes = Math.Round(incident.DurationSeconds / 60.0, 2)
                    });
                }
            }

            var orderedProblems = allProblems
                .OrderByDescending(p => p.SeverityLevel)
                .ThenByDescending(p => p.Started)
                .ToList();
            
            // Cache por 60 segundos
            var etag = $"\"{report.GeneratedAt.Ticks}\"";
            Response.Headers.Append("Cache-Control", "public, max-age=60");
            Response.Headers.Append("Last-Modified", report.GeneratedAt.ToUniversalTime().ToString("R"));
            Response.Headers.Append("ETag", etag);
            
            // Se cliente já tem a versão mais recente, retorna 304
            if (Request.Headers.ContainsKey("If-None-Match") && Request.Headers["If-None-Match"] == etag)
            {
                return StatusCode(304);
            }
            
            return Ok(orderedProblems);
        }

        [HttpGet("{clientId}/dashboard")]
        public async Task<IActionResult> GetDashboardByClient(string clientId)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            // ===== LÊ APENAS DO ARQUIVO JSON SALVO - SEM CHAMADAS AO ZABBIX =====
            var report = await _downtimeCalculation.GetSavedReportAsync(clientId);
            
            if (report == null)
            {
                return StatusCode(503, new { error = "Dados ainda não disponíveis. Aguarde o próximo ciclo de atualização." });
            }

            // Calcula disponibilidade
            var availability = report.TotalDowntimeSeconds > 0 
                ? Math.Round((1 - (report.TotalDowntimeSeconds / (double)(report.PeriodDays * 24 * 3600 * report.ServicesCount))) * 100, 2)
                : 100.0;

            // Conta INCIDENTES ativos e resolvidos direto do JSON
            int activeIncidentsCount = 0;
            int resolvedIncidentsCount = 0;
            
            foreach (var service in report.Services)
            {
                if (service.Incidents != null && service.Incidents.Any())
                {
                    activeIncidentsCount += service.Incidents.Count(i => i.IsActive);
                    resolvedIncidentsCount += service.Incidents.Count(i => !i.IsActive);
                }
            }

            Console.WriteLine($"[DASHBOARD][JSON] Incidentes Ativos: {activeIncidentsCount}");

            // Pega IPs dos hosts do config do Zabbix (não faz chamada, apenas lê config)
            var hostIps = _zabbix.GetUniqueHostIps().ToList();

            // Cache por 60 segundos (sincronizado com BackgroundService)
            var etag = $"\"{report.GeneratedAt.Ticks}\"";
            Response.Headers.Append("Cache-Control", "public, max-age=60");
            Response.Headers.Append("Last-Modified", report.GeneratedAt.ToUniversalTime().ToString("R"));
            Response.Headers.Append("ETag", etag);
            
            // Se cliente já tem a versão mais recente, retorna 304
            if (Request.Headers.ContainsKey("If-None-Match") && Request.Headers["If-None-Match"] == etag)
            {
                return StatusCode(304);
            }

            return Ok(new DashboardResponse
            {
                Host = new DashboardHost
                {
                    Name = $"{clientId.ToUpper()} - {hostIps.Count} host(s)",
                    Ip = string.Join(", ", hostIps),
                    Status = "Online",
                    Available = "1"
                },
                Availability = new DashboardAvailability
                {
                    Percent = availability,
                    DowntimeMinutes = report.TotalDowntimeSeconds / 60.0,
                    UptimeMinutes = (report.PeriodDays * 24 * 60.0 * report.ServicesCount) - (report.TotalDowntimeSeconds / 60.0),
                    TotalMinutes = report.PeriodDays * 24 * 60.0 * report.ServicesCount
                },
                Problems = new DashboardProblems
                {
                    Total = activeIncidentsCount + resolvedIncidentsCount,
                    Active = activeIncidentsCount,
                    Resolved = resolvedIncidentsCount
                },
                Triggers = new DashboardTriggers
                {
                    Total = 0,  // Não conta mais triggers em tempo real
                    Critical = 0,
                    Warning = 0
                }
            });
        }

        [HttpGet("clients")]
        public IActionResult GetClients()
        {
            var clientIds = _clientConfig.GetAllClientIds().ToList();
            return Ok(new { clients = clientIds });
        }

        /// <summary>
        /// Calcula o downtime de todos os serviços do cliente nos últimos N dias
        /// Baseado no script Python calculaDowntime.py
        /// </summary>
        [HttpGet("{clientId}/downtime/calculate")]
        public async Task<IActionResult> CalculateDowntime(string clientId, [FromQuery] int days = 30)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            if (days < 1 || days > 90)
                return BadRequest(new { error = "O parâmetro 'days' deve estar entre 1 e 90" });

            try
            {
                var report = await _downtimeCalculation.CalculateClientDowntimeAsync(clientId, days);
                
                if (report == null)
                    return StatusCode(500, new { error = "Erro ao gerar relatório de downtime" });

                return Ok(report);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao calcular downtime: {ex.Message}");
                return StatusCode(500, new { error = "Erro interno ao calcular downtime", details = ex.Message });
            }
        }

        /// <summary>
        /// Retorna o último relatório de downtime salvo
        /// </summary>
        [HttpGet("{clientId}/downtime/report")]
        public async Task<IActionResult> GetDowntimeReport(string clientId)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            try
            {
                var report = await _downtimeCalculation.GetSavedReportAsync(clientId);
                
                if (report == null)
                    return NotFound(new { error = "Nenhum relatório encontrado. Execute o cálculo primeiro." });

                // Cache por 60 segundos
                var etag = $"\"{report.GeneratedAt.Ticks}\"";
                Response.Headers.Append("Cache-Control", "public, max-age=60");
                Response.Headers.Append("Last-Modified", report.GeneratedAt.ToUniversalTime().ToString("R"));
                Response.Headers.Append("ETag", etag);
                
                // Se cliente já tem a versão mais recente, retorna 304
                if (Request.Headers.ContainsKey("If-None-Match") && Request.Headers["If-None-Match"] == etag)
                {
                    return StatusCode(304);
                }

                return Ok(report);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao buscar relatório: {ex.Message}");
                return StatusCode(500, new { error = "Erro ao buscar relatório", details = ex.Message });
            }
        }

        /// <summary>
        /// Retorna um resumo simplificado do downtime
        /// </summary>
        [HttpGet("{clientId}/downtime/summary")]
        public async Task<IActionResult> GetDowntimeSummary(string clientId)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            try
            {
                var report = await _downtimeCalculation.GetSavedReportAsync(clientId);
                
                if (report == null)
                    return StatusCode(503, new { error = "Dados ainda não disponíveis. Aguarde o próximo ciclo de atualização." });

                // Cache por 60 segundos
                var etag = $"\"{report.GeneratedAt.Ticks}\"";
                Response.Headers.Append("Cache-Control", "public, max-age=60");
                Response.Headers.Append("Last-Modified", report.GeneratedAt.ToUniversalTime().ToString("R"));
                Response.Headers.Append("ETag", etag);
                
                // Se cliente já tem a versão mais recente, retorna 304
                if (Request.Headers.ContainsKey("If-None-Match") && Request.Headers["If-None-Match"] == etag)
                {
                    return StatusCode(304);
                }

                // Retorna apenas o resumo
                return Ok(new
                {
                    clientId = report.ClientId,
                    periodDays = report.PeriodDays,
                    generatedAt = report.GeneratedAt,
                    totalDowntime = report.TotalDowntimeFormatted,
                    totalDowntimeSeconds = report.TotalDowntimeSeconds,
                    servicesCount = report.ServicesCount,
                    servicesWithDowntime = report.ServicesWithDowntime,
                    availability = report.TotalDowntimeSeconds > 0 
                        ? Math.Round((1 - (report.TotalDowntimeSeconds / (double)(report.PeriodDays * 24 * 3600 * report.ServicesCount))) * 100, 2)
                        : 100.0
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao buscar resumo: {ex.Message}");
                return StatusCode(500, new { error = "Erro ao buscar resumo", details = ex.Message });
            }
        }
    }

    public static class DateTimeExtensions
    {
        public static long ToUnixTimestamp(this DateTimeOffset dt) => dt.ToUnixTimeSeconds();
    }
}
