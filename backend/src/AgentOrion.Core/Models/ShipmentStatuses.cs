namespace AgentOrion.Core.Models;

public static class ShipmentStatuses
{
    public const string Requested = "solicitado";
    public const string Confirmed = "confirmado";
    public const string InTransit = "en_transito";
    public const string Delivered = "entregado";
    public const string Rejected = "rechazado";
    public const string Cancelled = "cancelado";

    public static bool IsValid(string status) =>
        status is Requested or Confirmed or InTransit or Delivered or Rejected or Cancelled;
}
