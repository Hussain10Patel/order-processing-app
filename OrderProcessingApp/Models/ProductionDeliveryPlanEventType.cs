namespace OrderProcessingApp.Models;

public enum ProductionDeliveryPlanEventType
{
    OpeningStock = 1,
    Order = 2,
    Production = 3,
    StockAdjustment = 4
}