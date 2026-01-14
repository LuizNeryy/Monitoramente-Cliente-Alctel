using monitor_services_api.Services;

namespace monitor_services_api.Services
{
    /// <summary>
    /// Serviço em background que atualiza os dados do dashboard periodicamente
    /// para todos os clientes configurados
    /// </summary>
    public class DashboardUpdateService : BackgroundService
    {
        private readonly ILogger<DashboardUpdateService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _updateInterval = TimeSpan.FromMinutes(1); // Atualiza a cada 1 minuto

        public DashboardUpdateService(
            ILogger<DashboardUpdateService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 DashboardUpdateService iniciado! Atualizando dados a cada {Interval} minuto(s)", _updateInterval.TotalMinutes);

            // Aguarda 10 segundos antes da primeira execução (para garantir que tudo iniciou)
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await UpdateAllClientsDataAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Erro ao atualizar dados dos clientes");
                }

                // Aguarda o intervalo configurado antes da próxima atualização
                await Task.Delay(_updateInterval, stoppingToken);
            }

            _logger.LogInformation("⏹️ DashboardUpdateService encerrado");
        }

        private async Task UpdateAllClientsDataAsync(CancellationToken cancellationToken)
        {
            var updateTime = DateTime.Now;
            _logger.LogInformation("⏰ [{Time}] Iniciando atualização automática dos dados...", updateTime.ToString("HH:mm:ss"));

            // Cria um escopo para resolver os serviços
            using var scope = _serviceProvider.CreateScope();
            
            var clientConfigService = scope.ServiceProvider.GetRequiredService<ClientConfigService>();
            var downtimeCalculationService = scope.ServiceProvider.GetRequiredService<DowntimeCalculationService>();
            var downtimeHistoryService = scope.ServiceProvider.GetRequiredService<DowntimeHistoryService>();
            
            var clientIds = clientConfigService.GetAllClientIds().ToList();
            
            if (!clientIds.Any())
            {
                _logger.LogWarning("⚠️ Nenhum cliente configurado para atualização");
                return;
            }

            _logger.LogInformation("📊 Atualizando dados de {Count} cliente(s): {Clients}", 
                clientIds.Count, string.Join(", ", clientIds));

            var tasks = new List<Task>();

            foreach (var clientId in clientIds)
            {
                // Atualiza cada cliente em paralelo
                tasks.Add(UpdateClientDataAsync(clientId, clientConfigService, downtimeCalculationService, downtimeHistoryService, cancellationToken));
            }

            await Task.WhenAll(tasks);

            var elapsed = DateTime.Now - updateTime;
            _logger.LogInformation("✅ Atualização concluída em {Elapsed}s", elapsed.TotalSeconds.ToString("F1"));
        }

        private async Task UpdateClientDataAsync(
            string clientId, 
            ClientConfigService clientConfigService,
            DowntimeCalculationService downtimeCalculationService,
            DowntimeHistoryService downtimeHistoryService,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("  🔄 [{ClientId}] Calculando downtime...", clientId);

                // Limpa registros de serviços que foram removidos do servicos.txt
                var currentServices = clientConfigService.GetClientServices(clientId);
                if (currentServices.Any())
                {
                    var serviceNames = new HashSet<string>(currentServices.Keys, StringComparer.OrdinalIgnoreCase);
                    downtimeHistoryService.CleanRemovedServices(clientId, serviceNames);
                }

                var report = await downtimeCalculationService.CalculateClientDowntimeAsync(clientId, 30);
                
                if (report != null)
                {
                    _logger.LogInformation("  ✓ [{ClientId}] Dados atualizados - Downtime: {Downtime}", 
                        clientId, report.TotalDowntimeFormatted);
                }
                else
                {
                    _logger.LogWarning("  ⚠️ [{ClientId}] Falha ao calcular downtime", clientId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "  ❌ [{ClientId}] Erro ao atualizar dados", clientId);
            }
        }
    }
}
