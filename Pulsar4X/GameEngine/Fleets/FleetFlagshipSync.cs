using Pulsar4X.Api;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Ships;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Keeps <see cref="FleetDB.FlagShipID"/> aligned with fleet children after entity transfer / ID reassignment.
    /// </summary>
    internal static class FleetFlagshipSync
    {
        internal static bool TryResolveFlagship(Entity fleet, FleetDB fleetDB, out Entity flagship)
        {
            flagship = Entity.InvalidEntity;
            if (fleet?.Manager == null)
                return false;

            if (fleetDB.FlagShipID >= 0
                && fleet.AttachedManager.TryGetEntityById(fleetDB.FlagShipID, out flagship)
                && flagship.Manager != null)
            {
                return true;
            }

            foreach (var child in fleetDB.Children)
            {
                if (child == null || !child.IsValid || child.Manager == null)
                    continue;
                if (!child.HasDataBlob<ShipInfoDB>())
                    continue;

                flagship = child;
                int staleId = fleetDB.FlagShipID;
                if (fleetDB.FlagShipID != child.Id)
                {
                    fleetDB.FlagShipID = child.Id;
                    if (staleId >= 0)
                    {
                        DebugTraceLog.Warn("Standing",
                            $"Fleet id={fleet.Id}: FlagShipID {staleId} → {child.Id} (stale after transfer/reassign)",
                            fleet.StarSysDateTime);
                    }
                }

                return true;
            }

            return false;
        }

        internal static bool TryGetFlagshipSystem(Entity fleet, out EntityManager? manager)
        {
            manager = null;
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;
            if (!TryResolveFlagship(fleet, fleetDB, out var flagship) || flagship.Manager == null)
                return false;

            manager = flagship.Manager;
            return true;
        }

        internal static void OnEntityIdReassigned(Game game, int oldId, int newId)
        {
            if (game == null || oldId == newId)
                return;

            UpdateFleetFlagshipIds(game.GlobalManager, oldId, newId);
            foreach (var system in game.Systems)
                UpdateFleetFlagshipIds(system, oldId, newId);
        }

        private static void UpdateFleetFlagshipIds(EntityManager manager, int oldId, int newId)
        {
            foreach (var fleetDB in manager.GetAllDataBlobsOfType<FleetDB>())
            {
                if (fleetDB.FlagShipID == oldId)
                    fleetDB.FlagShipID = newId;
            }
        }
    }
}
