using Microsoft.Extensions.Hosting.WindowsServices;
using monitor_services_api.Services;

var builder = WebApplication.CreateBuilder(
    new WebApplicationOptions
    {
        Args = args,
        // Quando roda como servi�o, o ContentRoot precisa ser a pasta do execut�vel
        ContentRootPath = WindowsServiceHelpers.IsWindowsService()
            ? AppContext.BaseDirectory
            : Directory.GetCurrentDirectory()
    });

builder.Host.UseWindowsService(o => o.ServiceName = "Monitor Service");

// Servi�os
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Registra o ClientConfigService como Singleton (carrega configs uma vez)
builder.Services.AddSingleton<ClientConfigService>();
builder.Services.AddSingleton<DowntimeHistoryService>();
builder.Services.AddScoped<DowntimeCalculationService>();

builder.Services.AddHttpClient<IZabbixService, ZabbixService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            (msg, cert, chain, err) => true
    });

// CORS: pol�tica permissiva para permitir todos os origins, m�todos e cabe�alhos.
// Uso SetIsOriginAllowed(_ => true) para permitir qualquer origem e AllowCredentials()
// caso precise enviar cookies/credenciais. Se n�o precisar de credenciais, prefira
// .AllowAnyOrigin() em vez de SetIsOriginAllowed.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy
            .SetIsOriginAllowed(_ => true) // permite qualquer origem
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()); // permita credenciais; remova se n�o necess�rio
});

var app = builder.Build();

// Inicializa o ClientConfigService (carrega clientes)
var clientConfigService = app.Services.GetRequiredService<ClientConfigService>();

// registra hora de in�cio para c�lculo de uptime no /health
var startTimeUtc = DateTime.UtcNow;

// Em servi�o normalmente o env ser� Production.
// Se quiser Swagger em produ��o, remova o IF.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Aplica CORS globalmente
app.UseCors("AllowAll");

app.UseStaticFiles();
app.MapControllers();

// Rota raiz
app.MapGet("/", () => Results.Redirect("/Monitor.html"));

// Rota cliente - precisa vir DEPOIS das rotas específicas
app.MapGet("/{clientId:regex(^[a-zA-Z0-9-]+$)}", (string clientId, ClientConfigService clientConfig) =>
{
    if (!clientConfig.ClientExists(clientId))
        return Results.NotFound(new { error = $"Cliente '{clientId}' não encontrado" });
    
    return Results.Redirect($"/Monitor.html?cliente={clientId}");
});

// Health endpoint simples
app.MapGet("/health", () =>
{
    var uptime = DateTime.UtcNow - startTimeUtc;
    var payload = new
    {
        status = "healthy",
        environment = app.Environment.EnvironmentName,
        started_at_utc = startTimeUtc.ToString("o"),
        uptime = uptime.ToString("c"),
        zabbix_server = builder.Configuration["Zabbix:Server"]
    };

    return Results.Ok(payload);
});

// Usa logger em vez de Console � mais adequado para servi�o
var logger = app.Logger;

logger.LogInformation("============================================================");
logger.LogInformation("Servidor de Monitoramento Iniciado!");
logger.LogInformation("Dashboard: https://localhost:4000");
logger.LogInformation("API Base: https://localhost:4000/api");
logger.LogInformation("Zabbix Server: {ZabbixServer}",
    builder.Configuration["Zabbix:Server"]);
logger.LogInformation("============================================================");

app.Run();