namespace OrderProcessingApp.Options;

public sealed class DeliveryLifecycleOptions
{
    public string BusinessTimeZone { get; set; } = "Africa/Johannesburg";
    public int PollIntervalSeconds { get; set; } = 60;
}
