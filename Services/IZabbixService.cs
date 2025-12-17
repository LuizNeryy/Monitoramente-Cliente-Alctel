using monitor_services_api.Models;

namespace monitor_services_api.Services
{
    public interface IZabbixService
    {
        Task<T> RequestAsync<T>(string method, object parameters) where T : class, new();
        Task<List<ZabbixHost>> GetHostsAsync(string ip);
        Task<List<ZabbixItem>> GetItemsAsync(string hostid, object? searchParams = null);
        Task<List<ZabbixTrigger>> GetTriggersAsync(string hostid);
        Task<List<ZabbixProblem>> GetProblemsAsync(string hostid, long? timeFrom = null);
        bool IsServicoMonitorado(string nomeServico);
        string? GetServiceIp(string nomeServico);
        IEnumerable<string> GetMonitoredServices();
        IEnumerable<string> GetUniqueHostIps();
        void SetCurrentClient(string clientId);
    }
}
