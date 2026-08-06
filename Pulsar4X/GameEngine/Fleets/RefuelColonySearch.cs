using System;
using System.Linq;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Storage;

namespace Pulsar4X.Fleets
{
    internal static class RefuelColonySearch
    {
        internal static Entity? FindNearestColonyInSystem(
            EntityManager manager,
            int factionId,
            PositionDB flagshipPos)
        {
            Entity? nearestColony = null;
            double nearestDist = double.MaxValue;

            foreach (var entity in manager.GetFilteredEntities(
                         EntityFilter.Friendly,
                         factionId,
                         e => e.HasDataBlob<ColonyInfoDB>() && e.HasDataBlob<CargoStorageDB>()))
            {
                if (!entity.TryGetDataBlob<PositionDB>(out var colonyPos))
                    continue;

                double dist = colonyPos.GetDistanceTo_m(flagshipPos);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestColony = entity;
                }
            }

            return nearestColony;
        }

        /// <summary>
        /// System to return to for fuel: last successful refuel, else any known colony system (shortest jump path).
        /// </summary>
        internal static bool TryResolveRefuelSystemId(
            Game game,
            Entity fleet,
            int factionId,
            FleetDB fleetDB,
            out string targetSystemId)
        {
            targetSystemId = string.Empty;
            if (fleet.AttachedManager == null)
                return false;

            string currentSystemId = fleet.AttachedManager.ManagerID ?? string.Empty;
            if (!game.Factions.TryGetValue(factionId, out var faction)
                || !faction.TryGetDataBlob<FactionInfoDB>(out var factionDB))
                return false;

            if (!string.IsNullOrEmpty(fleetDB.LastRefuelSystemId)
                && fleetDB.LastRefuelSystemId != currentSystemId
                && SystemHasRefuelColony(factionDB, fleetDB.LastRefuelSystemId))
            {
                targetSystemId = fleetDB.LastRefuelSystemId;
                return true;
            }

            double bestCost = double.MaxValue;
            string? bestSystem = null;

            foreach (var colony in factionDB.Colonies.Where(c => c.IsValid))
            {
                if (!colony.HasDataBlob<ColonyInfoDB>() || !colony.HasDataBlob<CargoStorageDB>())
                    continue;

                string colonySystem = colony.AttachedManager?.ManagerID ?? string.Empty;
                if (string.IsNullOrEmpty(colonySystem) || colonySystem == currentSystemId)
                    continue;

                if (!TryEstimateJumpPathCost(game, faction, currentSystemId, colonySystem, out var cost))
                    continue;

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestSystem = colonySystem;
                }
            }

            if (bestSystem == null)
                return false;

            targetSystemId = bestSystem;
            return true;
        }

        internal static bool TryFindJumpGateTowardSystem(
            Game game,
            Entity fleet,
            int factionId,
            string targetSystemId,
            PositionDB flagshipPos,
            out JumpPointDB? jumpGate)
        {
            jumpGate = null;
            if (fleet.AttachedManager == null || string.IsNullOrEmpty(targetSystemId))
                return false;

            if (!game.Factions.TryGetValue(factionId, out var faction)
                || !faction.TryGetDataBlob<FactionInfoDB>(out var factionDB))
                return false;

            string currentSystemId = fleet.AttachedManager.ManagerID ?? string.Empty;
            if (!factionDB.InternalKnownJumpPoints.TryGetValue(currentSystemId, out var currentJumpPoints)
                || currentJumpPoints.Count == 0)
                return false;

            // Direct jump link to the target system.
            foreach (var jpEntity in currentJumpPoints)
            {
                if (!jpEntity.TryGetDataBlob<JumpPointDB>(out var jpdb))
                    continue;
                if (!fleet.AttachedManager.TryGetGlobalEntityById(jpdb.DestinationId, out var destGate))
                    continue;
                if (destGate.AttachedManager?.ManagerID != targetSystemId)
                    continue;

                jumpGate = jpdb;
                return true;
            }

            if (!factionDB.InternalKnownJumpPoints.TryGetValue(targetSystemId, out var targetJumpPoints)
                || targetJumpPoints.Count == 0)
                return false;

            var pathfinding = new PathfindingManager(game);
            double bestCost = double.MaxValue;
            double bestDistToFleet = double.MaxValue;
            JumpPointDB? bestGate = null;

            foreach (var sourceJp in currentJumpPoints)
            {
                if (!sourceJp.TryGetDataBlob<JumpPointDB>(out var sourceJpdb))
                    continue;
                if (!sourceJp.TryGetDataBlob<PositionDB>(out var sourcePos))
                    continue;

                double distToFleet = sourcePos.GetDistanceTo_m(flagshipPos);

                foreach (var destJp in targetJumpPoints)
                {
                    try
                    {
                        var path = pathfinding.GetPath(sourceJp, destJp, out var cost);
                        if (cost >= double.MaxValue || path.Count == 0)
                            continue;

                        if (cost < bestCost || (Math.Abs(cost - bestCost) < 1e-6 && distToFleet < bestDistToFleet))
                        {
                            bestCost = cost;
                            bestDistToFleet = distToFleet;
                            bestGate = sourceJpdb;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Nodes not connected in faction graph.
                    }
                }
            }

            if (bestGate == null)
                return false;

            jumpGate = bestGate;
            return true;
        }

        internal static void RememberRefuelSite(FleetDB fleetDB, Entity colony)
        {
            if (colony.AttachedManager?.ManagerID is { Length: > 0 } systemId)
                fleetDB.LastRefuelSystemId = systemId;
            fleetDB.LastRefuelColonyId = colony.Id;
        }

        private static bool SystemHasRefuelColony(FactionInfoDB factionDB, string systemId)
        {
            return factionDB.Colonies.Any(c =>
                c.IsValid
                && c.AttachedManager?.ManagerID == systemId
                && c.HasDataBlob<ColonyInfoDB>()
                && c.HasDataBlob<CargoStorageDB>());
        }

        private static bool TryEstimateJumpPathCost(
            Game game,
            Entity faction,
            string fromSystemId,
            string toSystemId,
            out double cost)
        {
            cost = double.MaxValue;
            if (fromSystemId == toSystemId)
            {
                cost = 0;
                return true;
            }

            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionDB))
                return false;

            if (!factionDB.InternalKnownJumpPoints.TryGetValue(fromSystemId, out var fromJps)
                || !factionDB.InternalKnownJumpPoints.TryGetValue(toSystemId, out var toJps)
                || fromJps.Count == 0
                || toJps.Count == 0)
                return false;

            var pathfinding = new PathfindingManager(game);
            double best = double.MaxValue;

            foreach (var sourceJp in fromJps)
            {
                foreach (var destJp in toJps)
                {
                    try
                    {
                        pathfinding.GetPath(sourceJp, destJp, out var pathCost);
                        if (pathCost < best)
                            best = pathCost;
                    }
                    catch (InvalidOperationException)
                    {
                        // ignore
                    }
                }
            }

            if (best >= double.MaxValue)
                return false;

            cost = best;
            return true;
        }
    }
}
