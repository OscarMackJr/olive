namespace Olive.Application;

public enum OutboundDestination { Popeye, Hometown }

// Marks an interface as an outbound port and names the only system it may talk to (D17.1).
[AttributeUsage(AttributeTargets.Interface)]
public sealed class OutboundPortAttribute(OutboundDestination destination) : Attribute
{
    public OutboundDestination Destination { get; } = destination;
}
