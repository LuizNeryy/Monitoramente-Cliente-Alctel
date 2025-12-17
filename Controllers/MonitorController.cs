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

            var allServices = new List<ServiceResponse>();

            // Agrupa serviços por IP do host para otimizar chamadas
            var servicesByIp = _zabbix.GetMonitoredServices()
                .GroupBy(s => _zabbix.GetServiceIp(s))
                .Where(g => g.Key != null)
                .ToList();

            foreach (var group in servicesByIp)
            {
                var hostIp = group.Key!;
                var servicesToFind = group.ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Busca o host pelo IP
                var hosts = await _zabbix.GetHostsAsync(hostIp);
                if (!hosts.Any())
                {
                    Console.WriteLine($"⚠️ Host não encontrado para IP: {hostIp}");
                    continue;
                }

                var hostid = hosts[0].Hostid;
                var allItems = await _zabbix.GetItemsAsync(hostid, new { name = "*State of service*" });

                // Processa os serviços encontrados neste host
                foreach (var item in allItems)
                {
                    var name = item.Name;
                    if (name.Contains('"'))
                    {
                        var start = name.IndexOf('"') + 1;
                        var end = name.LastIndexOf('"');
                        if (start > 0 && end > start) name = name[start..end];
                    }
                    else if (name.Contains(':'))
                    {
                        name = name.Split(':', 2)[1].Trim();
                    }

                    // Verifica se é um dos serviços monitorados deste grupo
                    if (!servicesToFind.Contains(name)) continue;

                    var code = int.TryParse(item.Lastvalue, out var c) ? c : 255;
                    var serviceStatus = ServiceStatusMap.GetValueOrDefault(code, ("Unknown", false));

                    allServices.Add(new ServiceResponse
                    {
                        Name = name,
                        Status = serviceStatus.Item1,
                        Active = serviceStatus.Item2,
                        Lastcheck = long.TryParse(item.Lastclock, out var t)
                            ? FromUnixTime(t).ToString("yyyy-MM-dd HH:mm:ss") : "N/A"
                    });
                }
            }

            return Ok(allServices.OrderBy(s => s.Name));
        }

        [HttpGet("{clientId}/problems")]
        public async Task<IActionResult> GetProblemsByClient(string clientId, [FromQuery] int? resolved = null, [FromQuery] long? from = null)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            var allProblems = new List<ProblemResponse>();
            var timeFrom = from ?? ToUnixTime(DateTime.Now.AddDays(-30));
            
            Console.WriteLine($"[PROBLEMS] Buscando problemas para cliente '{clientId}' (resolved={resolved}, from={timeFrom})");

            // Lista de serviços monitorados (do txt)
            var monitoredServices = _zabbix.GetMonitoredServices().Select(s => s.Trim().ToLowerInvariant()).ToHashSet();

            foreach (var hostIp in _zabbix.GetUniqueHostIps())
            {
                var hosts = await _zabbix.GetHostsAsync(hostIp);
                if (!hosts.Any())
                {
                    Console.WriteLine($"⚠️ Host não encontrado para IP: {hostIp}");
                    continue;
                }

                var problems = await _zabbix.GetProblemsAsync(hosts[0].Hostid, timeFrom);

                foreach (var p in problems)
                {
                    // Extrai nome do serviço do problema
                    var problemName = p.Name;
                    if (problemName.Contains('"'))
                    {
                        var start = problemName.IndexOf('"') + 1;
                        var end = problemName.LastIndexOf('"');
                        if (start > 0 && end > start) problemName = problemName[start..end];
                    }
                    else if (problemName.Contains(':'))
                    {
                        problemName = problemName.Split(':', 2)[1].Trim();
                    }
                    var problemNameLower = problemName.Trim().ToLowerInvariant();
                    
                    // Filtra apenas serviços monitorados (do txt)
                    if (!monitoredServices.Contains(problemNameLower)) continue;

                    var clock = long.Parse(p.Clock);
                    var rClock = long.TryParse(p.R_clock, out var r) && r > 0 ? r : 0;
                    var now = ToUnixTime(DateTime.Now);
                    var duration = (rClock > 0 ? rClock : now) - clock;

                    // resolved=1: retorna apenas problemas resolvidos
                    if (resolved == 1)
                    {
                        if (rClock > 0 && rClock >= timeFrom)
                        {
                            allProblems.Add(new ProblemResponse
                            {
                                Name = p.Name,
                                Severity = PriorityMap.GetValueOrDefault(p.Severity, "Desconhecida"),
                                SeverityLevel = p.Severity,
                                Status = "Resolvido",
                                Started = FromUnixTime(clock).ToString("yyyy-MM-dd HH:mm:ss"),
                                DurationMinutes = Math.Round(duration / 60.0, 2)
                            });
                        }
                    }
                    // resolved=0 ou null: retorna apenas problemas ativos
                    else
                    {
                        if (rClock == 0)
                        {
                            allProblems.Add(new ProblemResponse
                            {
                                Name = p.Name,
                                Severity = PriorityMap.GetValueOrDefault(p.Severity, "Desconhecida"),
                                SeverityLevel = p.Severity,
                                Status = "Ativo",
                                Started = FromUnixTime(clock).ToString("yyyy-MM-dd HH:mm:ss"),
                                DurationMinutes = Math.Round(duration / 60.0, 2)
                            });
                        }
                    }
                }
            }

            var orderedProblems = allProblems
                .OrderByDescending(p => p.SeverityLevel)
                .ThenByDescending(p => p.Started)
                .ToList();

            Console.WriteLine($"[PROBLEMS] Total retornado: {orderedProblems.Count} (ativos={orderedProblems.Count(p => p.Status == "Ativo")}, resolvidos={orderedProblems.Count(p => p.Status == "Resolvido")})");
            return Ok(orderedProblems);
        }

        [HttpGet("{clientId}/dashboard")]
        public async Task<IActionResult> GetDashboardByClient(string clientId)
        {
            if (!ConfigureClientContext(clientId))
                return NotFound(new { error = $"Cliente '{clientId}' não encontrado" });

            var timeFrom = ToUnixTime(DateTime.Now.AddDays(-30));
            var timeTill = ToUnixTime(DateTime.Now);

            // NÃO limpa registros - mantém histórico completo para análise
            // _downtimeHistory.CleanOldRecords(clientId, ToUnixTime(DateTime.Now.AddDays(-60)));

            // Agregadores
            int totalServices = 0;
            double totalDowntimeSec = 0;
            int totalActiveProblems = 0;
            int totalResolvedProblems = 0;
            int totalTriggers = 0;
            int totalCritical = 0;
            int totalWarning = 0;
            var allHosts = new List<string>();
            var problemsProcessed = new HashSet<string>(); // Evita duplicação de problemas ativos entre hosts
            var resolvedProblemsProcessed = new HashSet<string>(); // Evita duplicação de problemas resolvidos

            // Processa cada host único
            foreach (var hostIp in _zabbix.GetUniqueHostIps())
            {
                var hosts = await _zabbix.GetHostsAsync(hostIp);
                if (!hosts.Any())
                {
                    Console.WriteLine($"⚠️ Host não encontrado para IP: {hostIp}");
                    continue;
                }

                var host = hosts[0];
                var hostid = host.Hostid;
                allHosts.Add($"{host.Name} ({hostIp})");

                // Serviços deste host - FILTRA APENAS OS DO TXT COM IP CORRETO
                var allItems = await _zabbix.GetItemsAsync(hostid, new { name = "*State of service*" });
                var hostServices = allItems.Where(s =>
                {
                    var name = s.Name;
                    if (name.Contains('"'))
                    {
                        var start = name.IndexOf('"') + 1;
                        var end = name.LastIndexOf('"');
                        if (start > 0 && end > start) name = name[start..end];
                    }
                    else if (name.Contains(':'))
                    {
                        name = name.Split(':', 2)[1].Trim();
                    }
                    // CRÍTICO: Valida serviço + IP para evitar duplicação entre hosts
                    var isMonitored = _zabbix.IsServicoMonitorado(name, hostIp);
                    if (isMonitored)
                        Console.WriteLine($"[SERVIÇO] {name} no host {hostIp} - monitorado");
                    return isMonitored;
                }).ToList();

                var hostServiceNames = hostServices.Select(s => {
                    var name = s.Name;
                    if (name.Contains('"')) {
                        var start = name.IndexOf('"') + 1;
                        var end = name.LastIndexOf('"');
                        if (start > 0 && end > start) name = name[start..end];
                    }
                    else if (name.Contains(':')) {
                        name = name.Split(':', 2)[1].Trim();
                    }
                    return name;
                }).ToList();
                
                Console.WriteLine($"[HOST {hostIp}] {hostServices.Count} serviços encontrados");
                Console.WriteLine($"[HOST {hostIp}] Serviços: {string.Join(", ", hostServiceNames)}");
                totalServices += hostServices.Count;

                // Verifica estado ATUAL dos serviços (para detectar serviços parados sem problema registrado)
                var servicesCurrentlyDown = new Dictionary<string, (long lastCheck, int statusCode)>();
                foreach (var item in hostServices)
                {
                    var name = item.Name;
                    if (name.Contains('"'))
                    {
                        var start = name.IndexOf('"') + 1;
                        var end = name.LastIndexOf('"');
                        if (start > 0 && end > start) name = name[start..end];
                    }
                    else if (name.Contains(':'))
                    {
                        name = name.Split(':', 2)[1].Trim();
                    }

                    var statusCode = int.TryParse(item.Lastvalue, out var c) ? c : 255;
                    
                    // Se o serviço NÃO está rodando (0 = Running)
                    if (statusCode != 0)
                    {
                        var lastCheck = long.TryParse(item.Lastclock, out var lc) ? lc : timeTill;
                        servicesCurrentlyDown[name.ToLower()] = (lastCheck, statusCode);
                        Console.WriteLine($"[SLA] Serviço PARADO detectado: {name} (código {statusCode})");
                    }
                }

                // Problemas deste host dos últimos 30 dias
                var problems = await _zabbix.GetProblemsAsync(hostid, timeFrom);
                var monitoredServices = _zabbix.GetMonitoredServices().Select(s => s.Trim().ToLowerInvariant()).ToHashSet();
                
                foreach (var p in problems)
                {
                    // Extrai nome do serviço do problema
                    var problemName = p.Name;
                    if (problemName.Contains('"'))
                    {
                        var start = problemName.IndexOf('"') + 1;
                        var end = problemName.LastIndexOf('"');
                        if (start > 0 && end > start) problemName = problemName[start..end];
                    }
                    else if (problemName.Contains(':'))
                    {
                        problemName = problemName.Split(':', 2)[1].Trim();
                    }
                    var problemNameLower = problemName.Trim().ToLowerInvariant();
                    
                    // Só processa problemas de serviços monitorados (do txt)
                    if (!monitoredServices.Contains(problemNameLower)) continue;

                    var clock = Math.Max(long.Parse(p.Clock), timeFrom);
                    var rClock = long.TryParse(p.R_clock, out var r) && r > 0 ? r : 0;

                    if (rClock > 0)
                    {
                        // Problema resolvido nos últimos 30 dias
                        if (rClock >= timeFrom && !resolvedProblemsProcessed.Contains(problemNameLower))
                        {
                            totalResolvedProblems++;
                            resolvedProblemsProcessed.Add(problemNameLower);
                            Console.WriteLine($"[RESOLVED] {problemName} - resolvido em {FromUnixTime(rClock):yyyy-MM-dd HH:mm}");
                        }
                        // Calcula downtime apenas dentro da janela de 30 dias
                        var downtimeStart = Math.Max(clock, timeFrom);
                        var downtimeEnd = Math.Min(rClock, timeTill);
                        var downtimeSecResolved = downtimeEnd - downtimeStart;
                        totalDowntimeSec += downtimeSecResolved;
                        // Salva no histórico se foi resolvido nos últimos 30 dias
                        if (rClock >= timeFrom)
                        {
                            _downtimeHistory.SaveDowntimeRecord(
                                clientId, 
                                problemNameLower, 
                                clock, 
                                rClock, 
                                downtimeSecResolved / 60.0
                            );
                        }
                    }
                    else
                    {
                        // Problema ativo
                        if (!problemsProcessed.Contains(problemNameLower))
                        {
                            totalActiveProblems++;
                            problemsProcessed.Add(problemNameLower);
                            Console.WriteLine($"[ACTIVE] {problemName} - ativo desde {FromUnixTime(clock):yyyy-MM-dd HH:mm}");
                        }
                        
                        // SEMPRE tenta salvar abertura (DowntimeHistoryService verifica duplicatas)
                        _downtimeHistory.SaveIncidentOpened(clientId, problemNameLower, clock);
                        
                        totalDowntimeSec += timeTill - clock;
                    }
                }

                // Verifica serviços parados que NÃO tiveram problema registrado
                Console.WriteLine($"[DEBUG] Serviços parados encontrados: {servicesCurrentlyDown.Count}");
                Console.WriteLine($"[DEBUG] Problemas ativos já processados: {string.Join(", ", problemsProcessed)}");
                
                foreach (var (serviceName, (lastCheck, statusCode)) in servicesCurrentlyDown)
                {
                    var serviceNameLower = serviceName.ToLower();
                    
                    // Se já foi processado como problema ativo, pula
                    if (problemsProcessed.Contains(serviceNameLower))
                    {
                        Console.WriteLine($"[DEBUG] Serviço {serviceName} JÁ tem problema ativo registrado - pulando");
                        continue;
                    }
                    
                    // Serviço está parado mas não há problema ativo registrado
                    // Conta como problema ativo e assume 30 dias de downtime (conservador)
                    var downtime = 30 * 24 * 3600L;
                    totalDowntimeSec += downtime;
                    totalActiveProblems++;
                    problemsProcessed.Add(serviceNameLower);
                    
                    Console.WriteLine($"[SLA] Serviço parado SEM problema registrado: {serviceName} (assumindo {downtime / 60.0:F0} min de downtime)");
                }

                // Triggers deste host
                var triggers = await _zabbix.GetTriggersAsync(hostid);
                totalTriggers += triggers.Count;
                totalCritical += triggers.Count(t => int.Parse(t.Priority) >= 4);
                totalWarning += triggers.Count(t => int.Parse(t.Priority) >= 2 && int.Parse(t.Priority) < 4);
            }

            // ===== BUSCA DADOS DO RELATÓRIO JSON =====
            var report = await _downtimeCalculation.GetSavedReportAsync(clientId);
            
            if (report == null)
            {
                // Se não houver relatório, calcula um novo
                report = await _downtimeCalculation.CalculateClientDowntimeAsync(clientId, 30);
            }

            if (report == null)
            {
                return StatusCode(500, new { error = "Erro ao obter dados de downtime" });
            }

            // Retorna os dados do JSON diretamente
            var availability = report.TotalDowntimeSeconds > 0 
                ? Math.Round((1 - (report.TotalDowntimeSeconds / (double)(report.PeriodDays * 24 * 3600 * report.ServicesCount))) * 100, 2)
                : 100.0;

            // Conta problemas ativos e resolvidos direto do JSON
            int activeProblemsFromJson = 0;
            int resolvedProblemsFromJson = 0;
            
            foreach (var service in report.Services)
            {
                if (service.Incidents != null && service.Incidents.Any())
                {
                    // Conta se tem pelo menos 1 incidente ativo
                    if (service.Incidents.Any(i => i.IsActive))
                    {
                        activeProblemsFromJson++;
                    }
                    // Conta se tem pelo menos 1 incidente resolvido
                    if (service.Incidents.Any(i => !i.IsActive))
                    {
                        resolvedProblemsFromJson++;
                    }
                }
            }

            Console.WriteLine($"[DASHBOARD][JSON] Total Downtime: {report.TotalDowntimeFormatted}");
            Console.WriteLine($"[DASHBOARD][JSON] Serviços: {report.ServicesCount}");
            Console.WriteLine($"[DASHBOARD][JSON] Disponibilidade: {availability:F2}%");
            Console.WriteLine($"[DASHBOARD][JSON] Problemas Ativos: {activeProblemsFromJson}");
            Console.WriteLine($"[DASHBOARD][JSON] Problemas Resolvidos: {resolvedProblemsFromJson}");
            Console.WriteLine($"[DASHBOARD][JSON] Total de Problemas: {activeProblemsFromJson + resolvedProblemsFromJson}");

            return Ok(new DashboardResponse
            {
                Host = new DashboardHost
                {
                    Name = $"{clientId.ToUpper()} - {_zabbix.GetUniqueHostIps().Count()} host(s)",
                    Ip = string.Join(", ", _zabbix.GetUniqueHostIps()),
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
                    Total = activeProblemsFromJson + resolvedProblemsFromJson,
                    Active = activeProblemsFromJson,
                    Resolved = resolvedProblemsFromJson
                },
                Triggers = new DashboardTriggers
                {
                    Total = totalTriggers,
                    Critical = totalCritical,
                    Warning = totalWarning
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
                {
                    // Se não houver relatório, calcula um novo
                    report = await _downtimeCalculation.CalculateClientDowntimeAsync(clientId, 30);
                }

                if (report == null)
                    return StatusCode(500, new { error = "Erro ao obter dados de downtime" });

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
