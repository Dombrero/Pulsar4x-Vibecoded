using System;
using System.Collections.Generic;
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
        /// System to return to for fuel: last successful refuel, else any known colony system
        /// (prefer direct jump links from the current system, then shortest known path).
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

            SeedLastRefuelFromColonies(fleetDB, factionDB, currentSystemId);
            SyncDiscoveredJumpPointsInSystem(faction, fleet.AttachedManager, factionId);

            if (!string.IsNullOrEmpty(fleetDB.LastRefuelSystemId)
                && fleetDB.LastRefuelSystemId != currentSystemId
                && SystemHasRefuelColony(factionDB, fleetDB.LastRefuelSystemId))
            {
                targetSystemId = fleetDB.LastRefuelSystemId;
                return true;
            }

            // Prefer a colony system reachable by a single jump from here (works even when
            // InternalKnownJumpPoints was never populated for older saves).
            if (TryFindDirectColonySystemViaLocalGates(
                    game, fleet.AttachedManager, factionDB, factionId, currentSystemId, out var directSystem))
            {
                targetSystemId = directSystem;
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

            // Last resort: any other-system colony even if path cost is unknown
            // (caller still needs a local gate toward it).
            if (bestSystem == null)
            {
                foreach (var colony in factionDB.Colonies.Where(c => c.IsValid))
                {
                    if (!colony.HasDataBlob<ColonyInfoDB>() || !colony.HasDataBlob<CargoStorageDB>())
                        continue;
                    string colonySystem = colony.AttachedManager?.ManagerID ?? string.Empty;
                    if (string.IsNullOrEmpty(colonySystem) || colonySystem == currentSystemId)
                        continue;
                    bestSystem = colonySystem;
                    break;
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

            SyncDiscoveredJumpPointsInSystem(faction, fleet.AttachedManager, factionId);

            string currentSystemId = fleet.AttachedManager.ManagerID ?? string.Empty;
            var currentJumpPoints = CollectKnownJumpPointsInSystem(
                game, factionDB, fleet.AttachedManager, factionId, currentSystemId);

            if (currentJumpPoints.Count == 0)
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

            var targetJumpPoints = CollectKnownJumpPointsInSystem(
                game, factionDB, null, factionId, targetSystemId);
            if (targetJumpPoints.Count == 0)
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

        private static void SeedLastRefuelFromColonies(
            FleetDB fleetDB,
            FactionInfoDB factionDB,
            string currentSystemId)
        {
            if (!string.IsNullOrEmpty(fleetDB.LastRefuelSystemId))
                return;

            foreach (var colony in factionDB.Colonies.Where(c => c.IsValid))
            {
                if (!colony.HasDataBlob<ColonyInfoDB>() || !colony.HasDataBlob<CargoStorageDB>())
                    continue;
                string colonySystem = colony.AttachedManager?.ManagerID ?? string.Empty;
                if (string.IsNullOrEmpty(colonySystem) || colonySystem == currentSystemId)
                    continue;

                fleetDB.LastRefuelSystemId = colonySystem;
                fleetDB.LastRefuelColonyId = colony.Id;
                return;
            }
        }

        /// <summary>
        /// Backfill InternalKnownJumpPoints from surveyed/discovered gates already in this system.
        /// Needed for saves that jumped before registration existed.
        /// </summary>
        private static void SyncDiscoveredJumpPointsInSystem(
            Entity faction,
            EntityManager manager,
            int factionId)
        {
            foreach (var jpEntity in manager.GetAllEntitiesWithDataBlob<JumpPointDB>())
            {
                if (!jpEntity.TryGetDataBlob<JumpPointDB>(out var jpdb))
                    continue;
                if (!jpdb.IsDiscovered.Contains(factionId))
                    continue;
                JumpPointKnowledge.Register(faction, jpEntity);
            }
        }

        private static bool TryFindDirectColonySystemViaLocalGates(
            Game game,
            EntityManager manager,
            FactionInfoDB factionDB,
            int factionId,
            string currentSystemId,
            out string colonySystemId)
        {
            colonySystemId = string.Empty;
            var colonySystems = new HashSet<string>();
            foreach (var colony in factionDB.Colonies.Where(c => c.IsValid))
            {
                if (!colony.HasDataBlob<ColonyInfoDB>() || !colony.HasDataBlob<CargoStorageDB>())
                    continue;
                string sys = colony.AttachedManager?.ManagerID ?? string.Empty;
                if (!string.IsNullOrEmpty(sys) && sys != currentSystemId)
                    colonySystems.Add(sys);
            }

            if (colonySystems.Count == 0)
                return false;

            foreach (var jpEntity in manager.GetAllEntitiesWithDataBlob<JumpPointDB>())
            {
                if (!jpEntity.TryGetDataBlob<JumpPointDB>(out var jpdb))
                    continue;
                if (!jpdb.IsDiscovered.Contains(factionId))
                    continue;
                if (!manager.TryGetGlobalEntityById(jpdb.DestinationId, out var destGate))
                    continue;
                string destSys = destGate.AttachedManager?.ManagerID ?? string.Empty;
                if (colonySystems.Contains(destSys))
                {
                    colonySystemId = destSys;
                    return true;
                }
            }

            return false;
        }

        private static List<Entity> CollectKnownJumpPointsInSystem(
            Game game,
            FactionInfoDB factionDB,
            EntityManager? localManager,
            int factionId,
            string systemId)
        {
            var result = new List<Entity>();
            var seen = new HashSet<int>();

            void Add(Entity e)
            {
                if (!e.IsValid || !seen.Add(e.Id))
                    return;
                result.Add(e);
            }

            if (factionDB.InternalKnownJumpPoints.TryGetValue(systemId, out var known))
            {
                foreach (var e in known)
                    Add(e);
            }

            if (localManager != null && localManager.ManagerID == systemId)
            {
                foreach (var jpEntity in localManager.GetAllEntitiesWithDataBlob<JumpPointDB>())
                {
                    if (!jpEntity.TryGetDataBlob<JumpPointDB>(out var jpdb))
                        continue;
                    if (!jpdb.IsDiscovered.Contains(factionId))
                        continue;
                    Add(jpEntity);
                }
            }
            else if (game.Systems.FirstOrDefault(s => s.ManagerID == systemId) is { } remote)
            {
                foreach (var jpEntity in remote.GetAllEntitiesWithDataBlob<JumpPointDB>())
                {
                    if (!jpEntity.TryGetDataBlob<JumpPointDB>(out var jpdb))
                        continue;
                    if (!jpdb.IsDiscovered.Contains(factionId))
                        continue;
                    Add(jpEntity);
                }
            }

            return result;
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

            var fromJps = CollectKnownJumpPointsInSystem(game, factionDB, null, faction.Id, fromSystemId);
            var toJps = CollectKnownJumpPointsInSystem(game, factionDB, null, faction.Id, toSystemId);
            if (fromJps.Count == 0 || toJps.Count == 0)
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
