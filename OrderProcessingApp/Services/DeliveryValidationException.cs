using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public sealed class DeliveryValidationException : InvalidOperationException
{
    public DeliveryValidationException(string message, int orderId, string orderNumber, OrderStatus currentStatus)
        : base(message)
    {
        OrderId = orderId;
        OrderNumber = orderNumber;
        CurrentStatus = currentStatus;
    }

    public int OrderId { get; }
    public string OrderNumber { get; }
    public OrderStatus CurrentStatus { get; }
}