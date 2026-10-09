using Microsoft.Extensions.Options;
using System.Globalization;
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

    public DateTimeOffset DepartureTime(DateTime scheduledDate, string? departureTime)
    {
        if (!TimeOnly.TryParseExact(departureTime, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time))
        {
            throw new InvalidOperationException("Departure time is required and must be a valid time in HH:mm format.");
        }
        return LocalDepartureTime(scheduledDate, time, BusinessTimeZone);
    }

    public static DateTimeOffset LocalDepartureTime(DateTime date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.Date.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

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
