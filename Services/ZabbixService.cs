using System.Text.Json;

using monitor_services_api.Models;

namespace monitor_services_api.Services
{
    public class ZabbixService : IZabbixService
    {
        private readonly HttpClient _http;
        private readonly string _defaultApiUrl;
        private readonly string _defaultApiToken;
        private readonly JsonSerializerOptions _jsonOpts;
        private readonly ClientConfigService _clientConfig;

        // Cache de contexto por cliente
        private string? _currentClientId;
        private ClientConfig? _currentConfig;
        private Dictionary<string, string>? _currentServices; // servico → IP

        public ZabbixService(HttpClient http, IConfiguration config, ClientConfigService clientConfig)
        {
            _http = http;
            _clientConfig = clientConfig;
            
            var server = config["Zabbix:Server"] ?? "https://monitoramento.alctel.com.br/zabbix";
            _defaultApiUrl = $"{server}/api_jsonrpc.php";
            _defaultApiToken = config["Zabbix:ApiToken"] ?? "";

            _jsonOpts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        public void SetCurrentClient(string clientId)
        {
            if (_currentClientId == clientId && _currentConfig != null)
                return; // Já está configurado

            _currentClientId = clientId;
            _currentConfig = _clientConfig.GetClientConfig(clientId);
            _currentServices = _clientConfig.GetClientServices(clientId);

            if (_currentConfig == null)
            {
                Console.WriteLine($"⚠️ Cliente '{clientId}' não encontrado");
                return;
            }

            // Configura autenticação para este cliente
            _http.DefaultRequestHeaders.Remove("Authorization");
            var token = _currentConfig.ZabbixApiToken ?? _defaultApiToken;
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            Console.WriteLine($"✓ Zabbix configurado para cliente '{clientId}' ({_currentServices?.Count ?? 0} serviços)");
        }

        public string? GetServiceIp(string nomeServico)
        {
            if (_currentServices == null)
                return null;

            return _currentServices.TryGetValue(nomeServico, out var ip) ? ip : null;
        }

        public bool IsServicoMonitorado(string nomeServico)
        {
            return _currentServices?.ContainsKey(nomeServico) ?? false;
        }

        public bool IsServicoMonitorado(string nomeServico, string hostIp)
        {
            if (_currentServices == null) return false;
            return _currentServices.TryGetValue(nomeServico, out var ip) && ip == hostIp;
        }

        public IEnumerable<string> GetMonitoredServices()
        {
            return _currentServices?.Keys ?? Enumerable.Empty<string>();
        }

        public IEnumerable<string> GetUniqueHostIps()
        {
            return _currentServices?.Values.Distinct() ?? Enumerable.Empty<string>();
        }

        public async Task<T> RequestAsync<T>(string method, object parameters) where T : class, new()
        {
            var apiUrl = _currentConfig?.ZabbixServer != null 
                ? $"{_currentConfig.ZabbixServer}/api_jsonrpc.php" 
                : _defaultApiUrl;

            var request = new ZabbixRequest { Method = method, Params = parameters };

            try
            {
                var response = await _http.PostAsJsonAsync(apiUrl, request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ZabbixResponse<T>>(json, _jsonOpts);

                if (result?.Error != null)
                {
                    Console.WriteLine($"Erro Zabbix: {result.Error.Message}");
                    return default;
                }

                return result?.Result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro na requisição: {ex.Message}");
                return default;
            }
        }

        public async Task<List<ZabbixHost>> GetHostsAsync(string ip)
        {
            // Tenta primeiro com filter exato
            var hosts = await RequestAsync<List<ZabbixHost>>("host.get", new
            {
                output = new[] { "hostid", "host", "name", "status", "available" },
                selectInterfaces = new[] { "ip", "available", "error" },
                filter = new { ip }
            }) ?? new List<ZabbixHost>();

            // Se não encontrou, tenta buscar por interface com esse IP
            if (!hosts.Any())
            {
                Console.WriteLine($"Tentando busca alternativa para IP: {ip}");
                hosts = await RequestAsync<List<ZabbixHost>>("host.get", new
                {
                    output = new[] { "hostid", "host", "name", "status", "available" },
                    selectInterfaces = new[] { "ip", "available", "error" }
                }) ?? new List<ZabbixHost>();

                // Filtra manualmente pelos que têm interface com esse IP
                hosts = hosts.Where(h => 
                    h.Interfaces?.Any(i => i.Ip?.Trim() == ip.Trim()) ?? false
                ).ToList();
            }

            return hosts;
        }

        public async Task<List<ZabbixItem>> GetItemsAsync(string hostid, object? searchParams = null)
        {
            var p = new Dictionary<string, object>
            {
                ["output"] = new[] { "itemid", "name", "lastvalue", "units", "lastclock", "status", "key_" },
                ["hostids"] = hostid,
                ["sortfield"] = "name",
                ["filter"] = new { status = 0 }
            };

            if (searchParams != null)
            {
                p["search"] = searchParams;
                p["searchWildcardsEnabled"] = true;
            }

            return await RequestAsync<List<ZabbixItem>>("item.get", p) ?? new List<ZabbixItem>();
        }

        public async Task<List<ZabbixTrigger>> GetTriggersAsync(string hostid)
        {
            return await RequestAsync<List<ZabbixTrigger>>("trigger.get", new
            {
                output = new[] { "triggerid", "description", "priority", "value", "lastchange" },
                hostids = hostid,
                sortfield = "priority",
                sortorder = "DESC",
                limit = 20,
                filter = new { value = 1 }
            }) ?? new List<ZabbixTrigger>();
        }

        public async Task<List<ZabbixProblem>> GetProblemsAsync(string hostid, long? timeFrom = null)
        {
            // Retorna TODOS os problemas (ativos + resolvidos) do host
            // O filtro por serviço monitorado será feito no Controller
            var p = new Dictionary<string, object>
            {
                ["output"] = new[] { "eventid", "clock", "r_clock", "name", "severity" },
                ["hostids"] = hostid,
                ["recent"] = false,
                ["sortfield"] = new[] { "eventid" },
                ["sortorder"] = "DESC"
            };

            if (timeFrom.HasValue) p["time_from"] = timeFrom.Value;

            return await RequestAsync<List<ZabbixProblem>>("problem.get", p) ?? new List<ZabbixProblem>();
        }
    }
}
