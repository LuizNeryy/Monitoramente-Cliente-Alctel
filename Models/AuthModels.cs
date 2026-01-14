namespace monitor_services_api.Models
{
    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginResponse
    {
        public string Token { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }

    public class AuthenticatedClient
    {
        public string ClientId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
    }
}
