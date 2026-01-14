using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using monitor_services_api.Models;
using monitor_services_api.Services;

namespace monitor_services_api.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _authService;
        private readonly ILogger<AuthController> _logger;

        public AuthController(AuthService authService, ILogger<AuthController> logger)
        {
            _authService = authService;
            _logger = logger;
        }

        /// <summary>
        /// Endpoint de login - recebe usuário e senha, retorna token JWT
        /// </summary>
        [HttpPost("login")]
        [AllowAnonymous]
        public IActionResult Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(new { error = "Usuário e senha são obrigatórios" });
            }

            var response = _authService.AuthenticateClient(request.Username, request.Password);

            if (response == null)
            {
                return Unauthorized(new { error = "Usuário ou senha inválidos" });
            }

            return Ok(response);
        }

        /// <summary>
        /// Endpoint para validar se o token ainda está válido
        /// </summary>
        [HttpGet("validate")]
        [Authorize]
        public IActionResult ValidateToken()
        {
            var clientId = AuthService.GetClientIdFromToken(User);
            var username = User.FindFirst("username")?.Value;

            return Ok(new
            {
                valid = true,
                clientId,
                username
            });
        }
    }
}
