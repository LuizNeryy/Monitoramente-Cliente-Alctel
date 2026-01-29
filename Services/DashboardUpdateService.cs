using monitor_services_api.Services;

namespace monitor_services_api.Services
{
    /// <summary>
    /// Serviço em background que atualiza os dados do dashboard periodicamente
    /// para todos os clientes configurados.
    /// Possui mecanismo de auto-recuperação que reinicia o loop se travar.
    /// </summary>
    public class DashboardUpdateService : BackgroundService
    {
        private readonly ILogger<DashboardUpdateService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _updateInterval = TimeSpan.FromMinutes(1); // Atualiza a cada 1 minuto
        private DateTime _lastSuccessfulUpdate = DateTime.MinValue;

        public DateTime LastSuccessfulUpdate => _lastSuccessfulUpdate;

        public DashboardUpdateService(
            ILogger<DashboardUpdateService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DashboardUpdateService iniciado - Intervalo: {Interval} minuto(s)", _updateInterval.TotalMinutes);

            // Aguarda 10 segundos antes da primeira execução
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            // Loop principal simples e estável
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await UpdateAllClientsDataAsync(stoppingToken);
                }
                catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Cancelamento normal - sai do loop
                    break;
                }
                catch (Exception ex)
                {
                    // Log do erro mas continua o loop
                    _logger.LogError(ex, "Erro ao atualizar dados dos clientes");
                }

                try
                {
                    // Aguarda o intervalo configurado antes da próxima atualização
                    await Task.Delay(_updateInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // Cancelamento durante o delay - sai do loop
                    break;
                }
            }

            _logger.LogInformation("DashboardUpdateService encerrado");
        }

        private async Task UpdateAllClientsDataAsync(CancellationToken cancellationToken)
        {
            var updateTime = DateTime.Now;
            _logger.LogInformation("[{Time}] Iniciando atualizacao automatica...", updateTime.ToString("HH:mm:ss"));

            // Cria um escopo para resolver os serviços
            using var scope = _serviceProvider.CreateScope();
            
            var clientConfigService = scope.ServiceProvider.GetRequiredService<ClientConfigService>();
            var downtimeCalculationService = scope.ServiceProvider.GetRequiredService<DowntimeCalculationService>();
            var downtimeHistoryService = scope.ServiceProvider.GetRequiredService<DowntimeHistoryService>();
            
            var clientIds = clientConfigService.GetAllClientIds().ToList();
            
            if (!clientIds.Any())
            {
                _logger.LogWarning("Nenhum cliente configurado");
                return;
            }

            _logger.LogInformation("Atualizando {Count} cliente(s): {Clients}", 
                clientIds.Count, string.Join(", ", clientIds));

            var tasks = new List<Task>();

            foreach (var clientId in clientIds)
            {
                // Atualiza cada cliente em paralelo
                tasks.Add(UpdateClientDataAsync(clientId, clientConfigService, downtimeCalculationService, downtimeHistoryService, cancellationToken));
            }

            await Task.WhenAll(tasks);

            _lastSuccessfulUpdate = DateTime.Now;
            var elapsed = DateTime.Now - updateTime;
            _logger.LogInformation("Atualizacao concluida em {Elapsed}s", elapsed.TotalSeconds.ToString("F1"));
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
                _logger.LogDebug("[{ClientId}] Calculando downtime...", clientId);

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
                    _logger.LogInformation("[{ClientId}] OK - Downtime: {Downtime}", 
                        clientId, report.TotalDowntimeFormatted);
                }
                else
                {
                    _logger.LogWarning("[{ClientId}] Falha ao calcular", clientId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ClientId}] Erro ao atualizar", clientId);
            }
        }
    }
}
