using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderProcessingApp.Options;

namespace OrderProcessingApp.Services;

public sealed class DeliveryLifecycleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeliveryClock _clock;
    private readonly DeliveryLifecycleOptions _options;
    private readonly ILogger<DeliveryLifecycleWorker> _logger;

    public DeliveryLifecycleWorker(IServiceScopeFactory scopeFactory, DeliveryClock clock,
        IOptions<DeliveryLifecycleOptions> options, ILogger<DeliveryLifecycleWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Delivery lifecycle worker started. BusinessTimeZone={TimeZone}, PollIntervalSeconds={Interval}",
            _clock.BusinessTimeZone.Id, _options.PollIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollIntervalSeconds));
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<DeliveryLifecycleService>()
                    .CompleteDueOrdersAsync(stoppingToken);
                _logger.LogInformation("Delivery lifecycle check completed. Estimated deliveries={Count}", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (DbUpdateException exception)
            {
                _logger.LogError(exception, "Delivery lifecycle database update failed; will retry at the next interval.");
            }
            catch (System.Data.Common.DbException exception)
            {
                _logger.LogError(exception, "Delivery lifecycle database connection failed; will retry at the next interval.");
            }
            catch (TimeoutException exception)
            {
                _logger.LogError(exception, "Delivery lifecycle check timed out; will retry at the next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
