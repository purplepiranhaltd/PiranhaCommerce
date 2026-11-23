using PiranhaCommerce.Core.Events;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// The events the shop publishes (sent as webhooks when that package is installed). Each carries ids, the order reference, states and money, never
/// names, emails or addresses: a receiver fetches what it needs from the API.
/// </summary>
public static class EcommerceEvents
{
    public const string OrderPlaced = "order.placed";
    public const string OrderStageChanged = "order.stage_changed";
    public const string PaymentReceived = "payment.received";
    public const string PaymentRefunded = "payment.refunded";
    public const string ShipmentCreated = "shipment.created";
    public const string ReturnCreated = "return.created";
    public const string DisputeOpened = "dispute.opened";
    public const string DisputeClosed = "dispute.closed";

    internal static object Order(Guid orderId, long number) => new { orderId, number, reference = OrderNumbers.Format(number) };
}

internal sealed class EcommerceEventTypes : IEventTypeContributor
{
    public IEnumerable<EventTypeInfo> GetEventTypes() =>
    [
        new(EcommerceEvents.OrderPlaced, "An order was placed at the checkout."),
        new(EcommerceEvents.OrderStageChanged, "An order moved to another stage (processing, completed, cancelled...)."),
        new(EcommerceEvents.PaymentReceived, "A payment was recorded against an order."),
        new(EcommerceEvents.PaymentRefunded, "A refund was recorded against an order."),
        new(EcommerceEvents.ShipmentCreated, "A parcel was recorded as sent."),
        new(EcommerceEvents.ReturnCreated, "A return was recorded."),
        new(EcommerceEvents.DisputeOpened, "A customer disputed a payment."),
        new(EcommerceEvents.DisputeClosed, "A payment dispute was won or lost."),
    ];
}
