namespace Pulsar4X.Api;

/// <summary>
/// Optional server capability for co-located clients: read the live fleet command tree without
/// waiting for the next push event (single-player / in-process adapter).
/// </summary>
public interface IFleetHierarchyReader
{
    (IReadOnlyList<FleetSnapshot> Fleets, IReadOnlyList<ShipSnapshot> UnattachedShips) GetFleetHierarchy(int factionId);
}
