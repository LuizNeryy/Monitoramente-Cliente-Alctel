using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using monitor_services_api.Models;

namespace monitor_services_api.Services
{
    public class AuthService
    {
        private readonly ClientConfigService _clientConfig;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AuthService> _logger;

        public AuthService(ClientConfigService clientConfig, IConfiguration configuration, ILogger<AuthService> logger)
        {
            _clientConfig = clientConfig;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Autentica um cliente com usuário e senha
        /// </summary>
        public LoginResponse? AuthenticateClient(string username, string password)
        {
            _logger.LogInformation($"Tentativa de login do usuário: {username}");

            // 1. Verifica se é um usuário global (acesso a todos os clientes)
            var globalUsers = _configuration.GetSection("GlobalUsers").Get<List<UserCredential>>();
            if (globalUsers != null)
            {
                var globalUser = globalUsers.FirstOrDefault(u => 
                    u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                
                if (globalUser != null && VerifyPassword(password, globalUser.PasswordHash))
                {
                    _logger.LogInformation($"Login global bem-sucedido: {username}");
                    
                    // Usuário global retorna com clientId vazio (frontend deve escolher)
                    var token = GenerateJwtToken("global", username);
                    var expiresAt = DateTime.UtcNow.AddHours(8);

                    return new LoginResponse
                    {
                        Token = token,
                        ClientId = "global",
                        ClientName = "Acesso Global (Alctel)",
                        ExpiresAt = expiresAt
                    };
                }
            }

            // 2. Busca todos os clientes para verificar usuários específicos
            var clientIds = _clientConfig.GetAllClientIds();
            
            foreach (var clientId in clientIds)
            {
                var config = _clientConfig.GetClientConfig(clientId);
                
                if (config == null)
                    continue;

                // 2a. Verifica na lista de múltiplos usuários (Users)
                if (config.Users != null && config.Users.Count > 0)
                {
                    var user = config.Users.FirstOrDefault(u => 
                        u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                    
                    if (user != null && (VerifyPassword(password, user.PasswordHash) || VerifyMasterPassword(password)))
                    {
                        _logger.LogInformation($"Login bem-sucedido: {username} (cliente: {clientId})");
                        
                        var token = GenerateJwtToken(clientId, username);
                        var expiresAt = DateTime.UtcNow.AddHours(8);

                        return new LoginResponse
                        {
                            Token = token,
                            ClientId = clientId,
                            ClientName = config.ClientName,
                            ExpiresAt = expiresAt
                        };
                    }
                }

                // 2b. Retrocompatibilidade: verifica usuário único (Username/PasswordHash)
                if (!string.IsNullOrEmpty(config.Username) && 
                    config.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    if (VerifyPassword(password, config.PasswordHash ?? "") || VerifyMasterPassword(password))
                    {
                        _logger.LogInformation($"Login bem-sucedido: {username} (cliente: {clientId})");
                        
                        var token = GenerateJwtToken(clientId, username);
                        var expiresAt = DateTime.UtcNow.AddHours(8);

                        return new LoginResponse
                        {
                            Token = token,
                            ClientId = clientId,
                            ClientName = config.ClientName,
                            ExpiresAt = expiresAt
                        };
                    }
                    else
                    {
                        _logger.LogWarning($"Senha incorreta para o usuário: {username}");
                        return null;
                    }
                }
            }

            _logger.LogWarning($"Usuário não encontrado: {username}");
            return null;
        }

        /// <summary>
        /// Gera um token JWT para o cliente
        /// </summary>
        private string GenerateJwtToken(string clientId, string username)
        {
            var jwtKey = _configuration["Jwt:Key"] ?? "MonitorServicesAlctelSecretKey2024!@#$MinimumLength32Characters";
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, username),
                new Claim("clientId", clientId),
                new Claim("username", username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"] ?? "MonitorServicesAlctel",
                audience: _configuration["Jwt:Audience"] ?? "MonitorServicesClients",
                claims: claims,
                expires: DateTime.UtcNow.AddHours(8),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Hash de senha usando SHA256
        /// </summary>
        public static string HashPassword(string password)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Verifica se a senha corresponde ao hash
        /// </summary>
        private static bool VerifyPassword(string password, string passwordHash)
        {
            if (string.IsNullOrEmpty(passwordHash))
                return false;

            var hash = HashPassword(password);
            return hash == passwordHash;
        }

        /// <summary>
        /// Verifica se a senha é a senha master da Alctel
        /// </summary>
        private bool VerifyMasterPassword(string password)
        {
            var masterPassword = _configuration["MasterPassword"];
            if (string.IsNullOrEmpty(masterPassword))
                return false;

            return password == masterPassword;
        }

        /// <summary>
        /// Extrai o clientId do token JWT
        /// </summary>
        public static string? GetClientIdFromToken(ClaimsPrincipal user)
        {
            return user.FindFirst("clientId")?.Value;
        }
    }
}
