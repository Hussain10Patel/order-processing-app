using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.Options;
using OrderProcessingApp.Services;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddScoped<IPricingService, PricingService>();
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

// Seed data
await app.Services.SeedCoreDataAsync();

// ✅ Bind to Render port
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

app.Run();