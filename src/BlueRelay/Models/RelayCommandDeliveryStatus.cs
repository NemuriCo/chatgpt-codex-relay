namespace BlueRelay.Models;

public enum RelayCommandDeliveryStatus
{
    None,
    Queued,
    Delivering,
    Delivered,
    Failed
}
