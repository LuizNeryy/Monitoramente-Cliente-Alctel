using System.Text.Json;
using monitor_services_api.Models;

namespace monitor_services_api.Services
{
    public class ClientConfigService
    {
        private readonly string _clientsBasePath;
        private readonly Dictionary<string, ClientConfig> _configCache;
        private readonly IConfiguration _globalConfig;

        public ClientConfigService(IConfiguration config)
        {
            _globalConfig = config;
            _clientsBasePath = Path.Combine(AppContext.BaseDirectory, "clientes");
            _configCache = new Dictionary<string, ClientConfig>(StringComparer.OrdinalIgnoreCase);

            // Carrega configurações na inicialização
            LoadAllConfigs();
        }

        private void LoadAllConfigs()
        {
            if (!Directory.Exists(_clientsBasePath))
            {
                Console.WriteLine($"⚠️ Pasta de clientes não encontrada: {_clientsBasePath}");
                Directory.CreateDirectory(_clientsBasePath);
                return;
            }

            var clientDirs = Directory.GetDirectories(_clientsBasePath);
            foreach (var dir in clientDirs)
            {
                var clientId = Path.GetFileName(dir);
                var configPath = Path.Combine(dir, "config.json");

                if (File.Exists(configPath))
                {
                    try
                    {
                        var json = File.ReadAllText(configPath);
                        var config = JsonSerializer.Deserialize<ClientConfig>(json, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        if (config != null)
                        {
                            // Se não tem Zabbix específico, usa o global ou .env
                            config.ZabbixServer ??= _globalConfig["Zabbix:Server"]
                                ?? Environment.GetEnvironmentVariable("ZABBIX_SERVER");
                            config.ZabbixApiToken ??= _globalConfig["Zabbix:ApiToken"]
                                ?? Environment.GetEnvironmentVariable("ZABBIX_API_TOKEN");

                            _configCache[clientId] = config;
                            Console.WriteLine($"✓ Cliente '{clientId}' carregado: {config.ClientName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Erro ao carregar config de '{clientId}': {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"✓ Total de {_configCache.Count} cliente(s) configurado(s)");
        }

        public ClientConfig? GetClientConfig(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return null;

            _configCache.TryGetValue(clientId, out var config);
            return config;
        }

        public Dictionary<string, string> GetClientServices(string clientId)
        {
            var servicosPath = Path.Combine(_clientsBasePath, clientId, "servicos.txt");

            if (!File.Exists(servicosPath))
            {
                Console.WriteLine($"⚠️ Arquivo de serviços não encontrado para '{clientId}': {servicosPath}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var servicos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in File.ReadAllLines(servicosPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split(';', 2);
                if (parts.Length == 2)
                {
                    var servico = parts[0].Trim();
                    var ip = parts[1].Trim();
                    
                    if (!string.IsNullOrEmpty(servico) && !string.IsNullOrEmpty(ip))
                    {
                        servicos[servico] = ip;
                    }
                }
                else
                {
                    Console.WriteLine($"⚠️ Linha inválida em servicos.txt: {line}");
                }
            }

            return servicos;
        }

        public IEnumerable<string> GetAllClientIds()
        {
            return _configCache.Keys;
        }

        public bool ClientExists(string clientId)
        {
            return _configCache.ContainsKey(clientId ?? "");
        }
    }
}
