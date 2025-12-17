namespace monitor_services_api.Models
{
    public class ClientConfig
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public string? ZabbixServer { get; set; }
        public string? ZabbixApiToken { get; set; }
    }
}
