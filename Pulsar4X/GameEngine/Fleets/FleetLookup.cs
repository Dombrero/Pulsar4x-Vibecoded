using System.Collections.Concurrent;
using System.Linq;
using Pulsar4X.Engine;
using Pulsar4X.Ships;

namespace Pulsar4X.Fleets;

internal static class FleetLookup
{
    private static readonly ConcurrentDictionary<int, int> s_shipToFleet = new();

    internal static void RegisterShip(Entity ship, Entity fleet)
    {
        if (ship is not { IsValid: true } || fleet is not { IsValid: true })
            return;
        if (!ship.HasDataBlob<ShipInfoDB>() || !fleet.HasDataBlob<FleetDB>())
            return;
        s_shipToFleet[ship.Id] = fleet.Id;
    }

    internal static void UnregisterShip(Entity ship)
    {
        if (ship is { IsValid: true })
            s_shipToFleet.TryRemove(ship.Id, out _);
    }

    /// <summary>O(1) via membership index; falls back to a system scan.</summary>
    internal static Entity FindFleetContainingShip(Entity ship)
    {
        if (ship is not { IsValid: true })
            return Entity.InvalidEntity;

        if (s_shipToFleet.TryGetValue(ship.Id, out int fleetId)
            && ship.AttachedManager != null
            && ship.AttachedManager.Game != null)
        {
            foreach (var system in ship.AttachedManager.Game.Systems)
            {
                if (system.TryGetEntityById(fleetId, out var fleet)
                    && fleet.TryGetDataBlob<FleetDB>(out var db)
                    && db.Children.Any(c => c.Id == ship.Id))
                    return fleet;
            }

            s_shipToFleet.TryRemove(ship.Id, out _);
        }

        var manager = ship.AttachedManager;
        if (manager != null)
        {
            foreach (var entity in manager.GetAllEntitiesWithDataBlob<FleetDB>())
            {
                var db = entity.GetDataBlob<FleetDB>();
                if (db.Children.Any(c => c.Id == ship.Id))
                {
                    RegisterShip(ship, entity);
                    return entity;
                }
            }
        }

        var game = manager?.Game;
        return game != null ? FindFleetContainingShip(game, ship.Id) : Entity.InvalidEntity;
    }

    internal static Entity FindFleetContainingShip(Game game, int shipId)
    {
        foreach (var system in game.Systems)
        {
            foreach (var entity in system.GetAllEntitiesWithDataBlob<FleetDB>())
            {
                var db = entity.GetDataBlob<FleetDB>();
                if (db.Children.Any(c => c.Id == shipId))
                {
                    if (system.TryGetEntityById(shipId, out var ship))
                        RegisterShip(ship, entity);
                    return entity;
                }
            }
        }

        return Entity.InvalidEntity;
    }
}
