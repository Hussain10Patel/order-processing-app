using Microsoft.Extensions.Options;
using OrderProcessingApp.Options;

namespace OrderProcessingApp.Services;

public sealed class DeliveryClock
{
    public TimeZoneInfo BusinessTimeZone { get; }
    private readonly TimeProvider _timeProvider;

    public DeliveryClock(TimeProvider timeProvider, IOptions<DeliveryLifecycleOptions> options)
    {
        _timeProvider = timeProvider;
        BusinessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.BusinessTimeZone);
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public DateTime BusinessToday(DateTimeOffset now) =>
        TimeZoneInfo.ConvertTime(now, BusinessTimeZone).Date;

    public static DateTimeOffset ExpectedDeliveryTime(DateTimeOffset startedAt, decimal? hours)
    {
        if (!hours.HasValue || hours.Value <= 0)
        {
            throw new InvalidOperationException("Expected delivery duration must be a positive number of hours.");
        }

        try
        {
            var ticks = checked((long)decimal.Round(hours.Value * TimeSpan.TicksPerHour));
            if (ticks <= 0)
            {
                throw new InvalidOperationException("Expected delivery duration is too small to represent.");
            }
            return startedAt.AddTicks(ticks);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException("Expected delivery duration exceeds the supported timestamp range.", exception);
        }
    }
}
