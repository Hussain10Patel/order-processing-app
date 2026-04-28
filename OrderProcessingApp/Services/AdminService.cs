using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;

namespace OrderProcessingApp.Services;

public class AdminService : IAdminService
{
    private readonly AppDbContext _dbContext;

    public AdminService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ResetDataAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        await _dbContext.Database.ExecuteSqlRawAsync(@"
            TRUNCATE TABLE
                ""AuditLogs"",
                ""DeliverySchedules"",
                ""OrderItems"",
                ""Orders"",
                ""ProductionPlans""
            RESTART IDENTITY CASCADE;
        ", cancellationToken);

        await SeedDataExtensions.SeedDemoWorkflowDataForDevelopmentAsync(_dbContext, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}