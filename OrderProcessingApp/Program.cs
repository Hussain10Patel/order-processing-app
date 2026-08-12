using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderProcessingApp.Data;
using OrderProcessingApp.Options;
using OrderProcessingApp.Services;

var builder = WebApplication.CreateBuilder(args);

var jsonSources = builder.Configuration.Sources
    .OfType<JsonConfigurationSource>()
    .ToList();

foreach (var source in jsonSources)
{
    builder.Configuration.Sources.Remove(source);
}

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false);

var rawConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
var resolvedConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");

static string? SafeConnectionPreview(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    try
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var host = parts.FirstOrDefault(p => p.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))?.Substring("Host=".Length);
        var port = parts.FirstOrDefault(p => p.StartsWith("Port=", StringComparison.OrdinalIgnoreCase))?.Substring("Port=".Length);
        var database = parts.FirstOrDefault(p => p.StartsWith("Database=", StringComparison.OrdinalIgnoreCase))?.Substring("Database=".Length);
        var username = parts.FirstOrDefault(p => p.StartsWith("Username=", StringComparison.OrdinalIgnoreCase))?.Substring("Username=".Length);
        var sslMode = parts.FirstOrDefault(p => p.StartsWith("SSL Mode=", StringComparison.OrdinalIgnoreCase))?.Substring("SSL Mode=".Length);

        var hostSegment = string.IsNullOrWhiteSpace(host) ? "unknown" : host;
        var portSegment = string.IsNullOrWhiteSpace(port) ? "unknown" : port;
        var databaseSegment = string.IsNullOrWhiteSpace(database) ? "unknown" : database;
        var usernameSegment = string.IsNullOrWhiteSpace(username) ? "redacted" : "[redacted]";

        return $"Host={hostSegment};Port={portSegment};Database={databaseSegment};Username={usernameSegment};SSL Mode={sslMode ?? "unknown"}";
    }
    catch
    {
        return "[unparseable]";
    }
}

static string Sha256Hex(string? value)
{
    if (value is null)
    {
        return "<null>";
    }

    using var sha = SHA256.Create();
    var bytes = Encoding.UTF8.GetBytes(value);
    var hash = sha.ComputeHash(bytes);
    return Convert.ToHexString(hash);
}

static (string? Host, string? Port) ParseHostAndPortFromConnectionString(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return (null, null);
    }

    var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var host = parts.FirstOrDefault(p => p.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))?.Substring("Host=".Length);
    var port = parts.FirstOrDefault(p => p.StartsWith("Port=", StringComparison.OrdinalIgnoreCase))?.Substring("Port=".Length);

    return (host, port);
}

var rawHostAndPort = ParseHostAndPortFromConnectionString(rawConnectionString);
var configHostAndPort = ParseHostAndPortFromConnectionString(resolvedConnectionString);
var rawMatchesConfig = string.Equals(rawConnectionString, resolvedConnectionString, StringComparison.Ordinal);

Console.WriteLine("[CONFIG_DIAGNOSTIC] EnvironmentName=" + builder.Environment.EnvironmentName);
Console.WriteLine("[CONFIG_DIAGNOSTIC] RawEnv_ConnectionStrings__DefaultConnection_Exists=" + (rawConnectionString is not null));
Console.WriteLine("[CONFIG_DIAGNOSTIC] RawEnv_ConnectionStrings__DefaultConnection_Length=" + (rawConnectionString?.Length ?? 0));
Console.WriteLine("[CONFIG_DIAGNOSTIC] RawEnv_ConnectionStrings__DefaultConnection_Preview=" + SafeConnectionPreview(rawConnectionString));
Console.WriteLine("[CONFIG_DIAGNOSTIC] RawEnv_ConnectionStrings__DefaultConnection_SHA256=" + Sha256Hex(rawConnectionString));
Console.WriteLine("[CONFIG_DIAGNOSTIC] Config_GetConnectionString_DefaultConnection_Exists=" + (resolvedConnectionString is not null));
Console.WriteLine("[CONFIG_DIAGNOSTIC] Config_GetConnectionString_DefaultConnection_Preview=" + SafeConnectionPreview(resolvedConnectionString));
Console.WriteLine("[CONFIG_DIAGNOSTIC] Config_GetConnectionString_DefaultConnection_SHA256=" + Sha256Hex(resolvedConnectionString));
Console.WriteLine("[CONFIG_DIAGNOSTIC] RawEnv_ConnectionStrings__DefaultConnection_Matches_Config=" + rawMatchesConfig);
Console.WriteLine("[CONFIG_DIAGNOSTIC] Config_Host=" + (configHostAndPort.Host ?? "<null>"));
Console.WriteLine("[CONFIG_DIAGNOSTIC] Config_Port=" + (configHostAndPort.Port ?? "<null>"));
Console.WriteLine("[CONFIG_DIAGNOSTIC] RawEnv_Host=" + (rawHostAndPort.Host ?? "<null>"));
Console.WriteLine("[CONFIG_DIAGNOSTIC] RawEnv_Port=" + (rawHostAndPort.Port ?? "<null>"));

const string ReactCorsPolicy = "ReactFrontendPolicy";

// Add services
builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddPolicy(ReactCorsPolicy, policy =>
    {
        policy
            .AllowAnyOrigin() // TEMP: allows frontend later
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<CsvImportOptions>(
    builder.Configuration.GetSection("CsvImport")
);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// Services
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<IDistributionCentreResolver, DistributionCentreResolver>();
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<IPalletService, PalletService>();
builder.Services.AddScoped<IPendingCsvImportService, PendingCsvImportService>();
builder.Services.AddScoped<IPlanningService, PlanningService>();
builder.Services.AddScoped<IProductionService, ProductionService>();
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IExportService, ExportService>();
builder.Services.AddScoped<IPastelExportService, PastelExportService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAdminService, AdminService>();

var app = builder.Build();

// Swagger only in dev
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ❌ REMOVE HTTPS REDIRECTION (Render handles HTTPS)
// app.UseHttpsRedirection();

app.UseCors(ReactCorsPolicy);

app.UseAuthorization();

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Seed data
await app.Services.SeedCoreDataAsync();

// ✅ Bind to Render port
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");