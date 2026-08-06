using System.Linq;
using Pulsar4X.Engine;

namespace Pulsar4X.Fleets;

internal static class FleetLookup
{
    internal static Entity FindFleetContainingShip(Game game, int shipId)
    {
        foreach (var system in game.Systems)
        {
            foreach (var entity in system.GetAllEntitiesWithDataBlob<FleetDB>())
            {
                var db = entity.GetDataBlob<FleetDB>();
                if (db.Children.Any(c => c.Id == shipId))
                    return entity;
            }
        }

        return Entity.InvalidEntity;
    }
}
