using System;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Events;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Interfaces;
using Pulsar4X.Orbital;
using Pulsar4X.Storage;

namespace Pulsar4X.Ships
{
    public class LaunchComplexProcessor : IHotloopProcessor
    {
        public TimeSpan RunFrequency => TimeSpan.FromDays(1);
        public TimeSpan FirstRunOffset => TimeSpan.FromHours(4);
        public Type GetParameterType => typeof(LaunchComplexDB);

        public void Init(Game game) { }

        public void ProcessEntity(Entity entity, int deltaSeconds)
        {
            ProcessColonyNow(entity);
        }

        /// <summary>
        /// Assign queued hulls to free pads and attempt launches. Safe to call from industry
        /// completion so a finished ship does not wait an extra daily tick.
        /// </summary>
        public static void ProcessColonyNow(Entity entity)
        {
            if (!entity.TryGetDataBlob<LaunchComplexDB>(out var launchDB))
                return;

            AssignQueueToPads(launchDB, entity);
            ProcessPadLaunches(launchDB, entity);
        }

        public int ProcessManager(EntityManager manager, int deltaSeconds)
        {
            var entities = manager.GetAllEntitiesWithDataBlob<LaunchComplexDB>();
            foreach (var entity in entities)
            {
                ProcessEntity(entity, deltaSeconds);
            }
            return entities.Count;
        }

        /// <summary>
        /// Delivers a freshly assembled hull into the game world. Prefers a pad launch (fuel cost);
        /// if no suitable pad exists or fuel is unavailable, falls back to spawning the ship in a
        /// parking orbit so completed hulls are never left invisible in the launch queue.
        /// </summary>
        public static Entity DeliverAssembledShip(Entity colonyEntity, ShipDesign shipDesign, string shipName)
        {
            var faction = colonyEntity.GetFactionOwner;
            var gameTime = colonyEntity.StarSysDateTime;

            DebugTraceLog.Info("Production",
                $"DeliverAssembledShip: '{shipName}' design={shipDesign.UniqueID} mass={shipDesign.MassPerUnit:N0}kg colony=#{colonyEntity.Id}",
                gameTime);

            if (colonyEntity.TryGetDataBlob<LaunchComplexDB>(out var launchDB))
            {
                launchDB.LaunchQueue.Add(new LaunchQueueEntry
                {
                    DesignId = shipDesign.UniqueID,
                    ShipName = shipName
                });
                DebugTraceLog.Info("Launch",
                    $"Queued '{shipName}' for launch (queue={launchDB.LaunchQueue.Count}, pads={launchDB.Pads.Count})",
                    gameTime);

                ProcessColonyNow(colonyEntity);

                // Successful pad launch clears the pad; find the ship by name under the faction root.
                if (TryFindUnattachedShip(faction, shipName) is { } launched)
                {
                    DebugTraceLog.Info("Launch",
                        $"Pad launch succeeded for '{shipName}' → ship#{launched.Id}",
                        gameTime);
                    return launched;
                }

                // Still sitting in the queue (no/too-small pad) or on a pad waiting for fuel.
                bool stillQueued = launchDB.LaunchQueue.Any(e =>
                    e.DesignId == shipDesign.UniqueID && e.ShipName == shipName);
                var blockedPad = launchDB.Pads
                    .FirstOrDefault(kvp => kvp.Value.ShipDesignId == shipDesign.UniqueID
                                           && kvp.Value.ShipName == shipName);

                if (stillQueued)
                {
                    DebugTraceLog.Warn("Launch",
                        $"No suitable pad for '{shipName}' (mass {shipDesign.MassPerUnit:N0} kg). Falling back to orbital spawn.",
                        gameTime);
                    launchDB.LaunchQueue.RemoveAll(e =>
                        e.DesignId == shipDesign.UniqueID && e.ShipName == shipName);
                }
                else if (blockedPad.Value != null)
                {
                    DebugTraceLog.Warn("Launch",
                        $"Pad launch did not complete for '{shipName}' (fuel or CreateShip failure). Falling back to orbital spawn.",
                        gameTime);
                    ClearPad(blockedPad.Value);
                }
            }
            else
            {
                DebugTraceLog.Info("Launch",
                    $"Colony#{colonyEntity.Id} has no LaunchComplexDB — spawning '{shipName}' directly.",
                    gameTime);
            }

            return SpawnInParkingOrbit(colonyEntity, faction, shipDesign, shipName);
        }

        private static Entity? TryFindUnattachedShip(Entity faction, string shipName)
        {
            if (!faction.TryGetDataBlob<FleetDB>(out var fleetDB))
                return null;

            foreach (var child in fleetDB.GetChildren())
            {
                if (child.HasDataBlob<FleetDB>())
                    continue;
                if (child.GetName(faction.Id) == shipName)
                    return child;
            }
            return null;
        }

        private static Entity SpawnInParkingOrbit(Entity colonyEntity, Entity faction, ShipDesign shipDesign, string shipName)
        {
            var parent = colonyEntity.GetSOIParentEntity()
                         ?? colonyEntity.GetDataBlob<ColonyInfoDB>()?.PlanetEntity;
            if (parent == null)
            {
                DebugTraceLog.Error("Production",
                    $"Cannot spawn '{shipName}': colony#{colonyEntity.Id} has no SOI/planet parent.",
                    colonyEntity.StarSysDateTime);
                throw new InvalidOperationException(
                    $"Cannot spawn ship '{shipName}': colony has no orbital parent.");
            }

            var ship = ShipFactory.CreateShip(shipDesign, faction, parent, shipName);
            FleetHierarchy.AttachUnattachedShip(faction, ship);
            DebugTraceLog.Info("Production",
                $"Spawned '{shipName}' as ship#{ship.Id} in parking orbit around '{parent.GetName(faction.Id)}' (unattached).",
                colonyEntity.StarSysDateTime);
            return ship;
        }

        private static void ClearPad(LaunchPad pad)
        {
            pad.ShipDesignId = null;
            pad.ShipName = null;
            pad.ShipMass = 0;
            pad.TargetOrbitRadius = 0;
            pad.ReadyToLaunch = false;
            pad.LaunchFuelWarningIssued = false;
        }

        private static void AssignQueueToPads(LaunchComplexDB launchDB, Entity colonyEntity)
        {
            var gameTime = colonyEntity.StarSysDateTime;

            if (launchDB.Pads.Count == 0 && launchDB.LaunchQueue.Count > 0)
            {
                DebugTraceLog.Warn("Launch",
                    $"Colony#{colonyEntity.Id} has LaunchComplexDB but 0 pads; {launchDB.LaunchQueue.Count} hull(s) waiting.",
                    gameTime);
            }

            foreach (var kvp in launchDB.Pads)
            {
                var pad = kvp.Value;
                if (pad.ShipDesignId != null)
                    continue;

                for (int i = 0; i < launchDB.LaunchQueue.Count; i++)
                {
                    var entry = launchDB.LaunchQueue[i];

                    if (!colonyEntity.GetFactionOwner.TryGetDataBlob<FactionInfoDB>(out var factionInfo)
                        || !factionInfo.IndustryDesigns.TryGetValue(entry.DesignId, out var designInfo))
                    {
                        DebugTraceLog.Warn("Launch",
                            $"Queue entry '{entry.ShipName}' design '{entry.DesignId}' not in IndustryDesigns — skipping.",
                            gameTime);
                        continue;
                    }

                    var shipDesign = (ShipDesign)designInfo;
                    if (shipDesign.MassPerUnit > pad.MaxTonnage)
                    {
                        DebugTraceLog.Warn("Launch",
                            $"Pad {kvp.Key} max {pad.MaxTonnage:N0} kg < ship '{entry.ShipName}' {shipDesign.MassPerUnit:N0} kg.",
                            gameTime);
                        continue;
                    }

                    pad.ShipDesignId = entry.DesignId;
                    pad.ShipName = entry.ShipName;
                    pad.ShipMass = shipDesign.MassPerUnit;
                    pad.TargetOrbitRadius = 0;
                    pad.ReadyToLaunch = true;
                    pad.LaunchFuelWarningIssued = false;
                    launchDB.LaunchQueue.RemoveAt(i);
                    DebugTraceLog.Info("Launch",
                        $"Assigned '{entry.ShipName}' to pad {kvp.Key} (ready to launch).",
                        gameTime);
                    break;
                }
            }
        }

        private static void ProcessPadLaunches(LaunchComplexDB launchDB, Entity colonyEntity)
        {
            foreach (var kvp in launchDB.Pads.ToArray())
            {
                var pad = kvp.Value;
                if (pad.ShipDesignId == null || !pad.ReadyToLaunch)
                    continue;

                TryLaunchShip(colonyEntity, kvp.Key);
            }
        }

        public static bool TryLaunchShip(Entity colonyEntity, string padId)
        {
            var gameTime = colonyEntity.StarSysDateTime;

            if (!colonyEntity.TryGetDataBlob<LaunchComplexDB>(out var launchDB))
                return false;

            if (!launchDB.Pads.TryGetValue(padId, out var pad))
                return false;

            if (pad.ShipDesignId == null)
                return false;

            if (!colonyEntity.TryGetDataBlob<ColonyInfoDB>(out var colonyInfo))
            {
                DebugTraceLog.Error("Launch", $"Colony#{colonyEntity.Id} missing ColonyInfoDB.", gameTime);
                return false;
            }

            var planet = colonyInfo.PlanetEntity;
            var faction = colonyEntity.GetFactionOwner;

            // Prefer the live SOI parent (always manager-bound). ColonyInfoDB.PlanetEntity can be a
            // stale deserialized reference after load — CreateShip then NullRefs on parent.Manager.
            var orbitParent = colonyEntity.GetSOIParentEntity()
                              ?? (planet.IsValid ? planet : null);
            if (orbitParent?.Manager == null)
            {
                DebugTraceLog.Error("Launch",
                    $"Pad {padId}: no valid orbit parent for '{pad.ShipName}' (SOI/PlanetEntity unbound).",
                    gameTime);
                return false;
            }

            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo)
                || !factionInfo.IndustryDesigns.TryGetValue(pad.ShipDesignId, out var designInfo))
            {
                DebugTraceLog.Error("Launch",
                    $"Pad {padId}: design '{pad.ShipDesignId}' missing from IndustryDesigns.",
                    gameTime);
                return false;
            }

            var shipDesign = (ShipDesign)designInfo;

            var targetRadius = pad.TargetOrbitRadius > 0
                ? pad.TargetOrbitRadius
                : OrbitMath.LowOrbitRadius(orbitParent);

            double fuelCost = OrbitMath.FuelCostToOrbit(orbitParent, shipDesign.MassPerUnit, targetRadius);
            double fuelAvailable = GetFuelMassAvailable(colonyEntity);

            DebugTraceLog.Info("Launch",
                $"TryLaunch '{pad.ShipName}' pad={padId}: need {fuelCost:N0} kg fuel, have {fuelAvailable:N0} kg, orbitR={targetRadius:N0}m",
                gameTime);

            // Explicit gate — never call CreateShip when the colony cannot pay launch fuel.
            if (fuelAvailable + 1e-6 < fuelCost || !TryDeductFuel(colonyEntity, fuelCost))
            {
                if (!pad.LaunchFuelWarningIssued)
                {
                    pad.LaunchFuelWarningIssued = true;
                    DebugTraceLog.Warn("Launch",
                        $"Launch of '{pad.ShipName}' delayed: need ~{fuelCost:N0} kg fuel, have {fuelAvailable:N0} kg.",
                        gameTime);
                    try
                    {
                        EventManager.Instance.Publish(
                            Event.Create(
                                EventType.ProductionCompleted,
                                gameTime,
                                $"Launch of {pad.ShipName ?? shipDesign.Name} delayed: not enough fuel in colony storage (need ~{fuelCost:N0} kg).",
                                colonyEntity.FactionOwnerID,
                                colonyEntity.Manager.ManagerID,
                                colonyEntity.Id));
                    }
                    catch (Exception ex)
                    {
                        DebugTraceLog.Warn("Launch",
                            $"Fuel-warning event publish failed: {ex.GetType().Name}: {ex.Message}",
                            gameTime);
                    }
                }
                return false;
            }

            try
            {
                var position = new Vector3(targetRadius, 0, 0);
                var ship = ShipFactory.CreateShip(shipDesign, faction, position, orbitParent, pad.ShipName);
                FleetHierarchy.AttachUnattachedShip(faction, ship);
                DebugTraceLog.Info("Launch",
                    $"Launched '{pad.ShipName}' → ship#{ship.Id} (unattached).",
                    gameTime);
            }
            catch (Exception ex)
            {
                DebugTraceLog.Error("Launch",
                    $"CreateShip/Attach failed for '{pad.ShipName}': {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                    gameTime);
                return false;
            }

            ClearPad(pad);
            return true;
        }

        private static double GetFuelMassAvailable(Entity colonyEntity)
        {
            if (!colonyEntity.TryGetDataBlob<CargoStorageDB>(out var storage))
                return 0;
            if (!storage.TypeStores.TryGetValue("fuel-storage", out var fuelStore))
                return 0;

            double total = 0;
            foreach (var fuelItem in fuelStore.Cargoables.Values)
                total += storage.GetMassStored(fuelItem, false);
            return total;
        }

        private static bool TryDeductFuel(Entity colonyEntity, double fuelMassNeeded)
        {
            if (!colonyEntity.TryGetDataBlob<CargoStorageDB>(out var storage))
                return false;

            if (!storage.TypeStores.ContainsKey("fuel-storage"))
                return false;

            var fuelStore = storage.TypeStores["fuel-storage"];
            var cargoLib = colonyEntity.GetFactionCargoDefinitions();
            if (cargoLib == null)
                return false;

            double remaining = fuelMassNeeded;

            foreach (var kvp in fuelStore.Cargoables.ToArray())
            {
                if (remaining <= 0)
                    break;

                var fuelItem = kvp.Value;
                double storedMass = storage.GetMassStored(fuelItem, false);
                if (storedMass <= 0)
                    continue;

                double toDeduct = Math.Min(remaining, storedMass);
                CargoTransferProcessor.AddRemoveCargoMass(colonyEntity, fuelItem, -toDeduct);
                remaining -= toDeduct;
            }

            return remaining <= 0;
        }
    }
}
