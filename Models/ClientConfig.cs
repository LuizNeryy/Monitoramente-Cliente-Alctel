namespace monitor_services_api.Models
{
    public class ClientConfig
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public string? ZabbixServer { get; set; }
        public string? ZabbixApiToken { get; set; }
        public string? Username { get; set; }
        public string? PasswordHash { get; set; }
    }
}
