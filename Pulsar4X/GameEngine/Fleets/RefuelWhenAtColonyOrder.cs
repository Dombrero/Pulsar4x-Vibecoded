using System;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Storage;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Fleet order: issue WaitTillFull transfers for any fuel-needy ships already at the colony,
    /// wait for stragglers still warping in, and stay queued until every fuel tank is full.
    /// </summary>
    public class RefuelWhenAtColonyOrder : EntityCommand
    {
        public override string Name => "Refuel at Colony";

        public override string Details
        {
            get
            {
                if (!_entityCommanding.IsValid)
                    return "Waiting for fuel-needy ships at colony.";
                if (FleetOrderProcessor.FleetShipsHaveRefuelWork(_entityCommanding))
                    return "Fuel transfer in progress.";
                if (FleetFuel.AnyHasFreeTankSpace(_entityCommanding))
                    return "Waiting for fuel-needy ships at colony.";
                return "Refuel complete.";
            }
        }

        public override ActionLaneTypes ActionLanes { get; } =
            ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = Entity.InvalidEntity;
        private Entity _colony = Entity.InvalidEntity;
        private bool _gaveUp;

        internal override Entity EntityCommanding => _entityCommanding;

        public Entity Colony => _colony;

        public static RefuelWhenAtColonyOrder CreateCommand(int factionId, Entity fleet, Entity colony)
        {
            return new RefuelWhenAtColonyOrder
            {
                UseActionLanes = true,
                RequestingFactionGuid = factionId,
                EntityCommandingGuid = fleet.Id,
                _entityCommanding = fleet,
                _colony = colony,
                CreatedDate = fleet.StarSysDateTime,
            };
        }

        internal override bool IsFinished()
        {
            if (_gaveUp)
                return _isFinished = true;

            if (FleetOrderProcessor.FleetShipsHaveRefuelWork(_entityCommanding))
                return _isFinished = false;

            // Stay until every fuel-capable tank is full (WaitTillFull), not a % hysteresis band.
            if (FleetFuel.AnyHasFreeTankSpace(_entityCommanding))
                return _isFinished = false;

            return _isFinished = true;
        }

        internal override void Execute(DateTime atDateTime)
        {
            if (_isFinished || _gaveUp)
                return;

            IsRunning = true;

            if (!_colony.HasDataBlob<CargoStorageDB>())
            {
                DebugTraceLog.Warn("Refuel",
                    $"fleet#{_entityCommanding.Id}: colony#{_colony.Id} has no cargo storage",
                    atDateTime);
                _gaveUp = true;
                return;
            }

            if (!FleetFuel.AnyHasFreeTankSpace(_entityCommanding))
                return;

            if (FleetOrderProcessor.FleetShipsHaveRefuelWork(_entityCommanding))
                return;

            // Issue for whoever is already on-station — do not wait for full / non-needy siblings.
            try
            {
                bool ok = CargoTransferOrder.CreateRefuelFleetCommand(_colony, _entityCommanding, Source);

                if (!ok)
                {
                    FleetOrderCleanup.AbortCargoTransfersOnFleetShips(_entityCommanding);
                    if (_colony.TryGetDataBlob<CargoStorageDB>(out var colonyStore))
                        CargoTransferOrder.ReleaseOrphanEscrow(colonyStore);
                    ok = CargoTransferOrder.CreateRefuelFleetCommand(_colony, _entityCommanding, Source);
                }

                if (ok && _entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                    RefuelColonySearch.RememberRefuelSite(fleetDB, _colony);

                if (ok)
                {
                    // CreateRefuel can report success then leave no lasting ship work (instant
                    // finish / skipped). Avoid re-issuing every Orderable tick.
                    if (!FleetOrderProcessor.FleetShipsHaveRefuelWork(_entityCommanding)
                        && FleetFuel.AnyHasFreeTankSpace(_entityCommanding)
                        && FleetFuel.AreNeedyShipsAtColony(_entityCommanding, _colony))
                    {
                        DebugTraceLog.Warn("Refuel",
                            $"fleet#{_entityCommanding.Id}: CreateRefuel returned ok but no ship transfers — giving up at colony#{_colony.Id}",
                            atDateTime);
                        GiveUpRefuel(atDateTime, "transfer vanished immediately");
                        return;
                    }

                    DebugTraceLog.Info("Refuel",
                        $"fleet#{_entityCommanding.Id}: issued refuel transfers from colony#{_colony.Id} success=True",
                        atDateTime);
                    return;
                }

                if (!FleetFuel.AnyHasFreeTankSpace(_entityCommanding))
                {
                    if (_entityCommanding.TryGetDataBlob<FleetDB>(out var doneDb))
                        RefuelColonySearch.RememberRefuelSite(doneDb, _colony);
                    DebugTraceLog.Info("Refuel",
                        $"fleet#{_entityCommanding.Id}: at colony#{_colony.Id} — all fuel tanks full, nothing to issue",
                        atDateTime);
                    return;
                }

                // Still free tank space: either stragglers en route, or a real issue failure.
                if (!FleetFuel.AreNeedyShipsAtColony(_entityCommanding, _colony))
                {
                    DebugTraceLog.Info("Refuel",
                        $"fleet#{_entityCommanding.Id}: waiting for fuel-needy ships to reach colony#{_colony.Id}",
                        atDateTime);
                    return;
                }

                string reason = DescribeColonyFuelShortage(_colony, _entityCommanding);
                GiveUpRefuel(atDateTime, reason);
                DebugTraceLog.Warn("Refuel",
                    $"fleet#{_entityCommanding.Id}: refuel issue failed at colony#{_colony.Id} — {reason}",
                    atDateTime);
            }
            catch (Exception ex)
            {
                DebugTraceLog.Error("Refuel",
                    $"fleet#{_entityCommanding.Id}: CreateRefuelFleetCommand failed: {ex.Message}",
                    atDateTime);
                System.Diagnostics.Debug.WriteLine($"RefuelWhenAtColony failed: {ex}");
                GiveUpRefuel(atDateTime, "exception");
            }
        }

        private void GiveUpRefuel(DateTime atDateTime, string reason)
        {
            if (_entityCommanding.TryGetDataBlob<FleetDB>(out var suppressDb))
            {
                suppressDb.ActiveStandingOrderIndex = -1;
                // Empty colony / vanished transfer: back off a week so we don't spam every 2 days.
                suppressDb.StandingSuppressUntil = atDateTime + TimeSpan.FromDays(7);
                suppressDb.StandingStatusMessage = $"Can't refuel at colony — {reason}";
            }
            _gaveUp = true;
        }

        private static string DescribeColonyFuelShortage(Entity colony, Entity fleet)
        {
            if (!colony.TryGetDataBlob<CargoStorageDB>(out var colonyStore))
                return "colony has no cargo storage";

            try
            {
                var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                foreach (var ship in FleetFuel.ShipsNeedingRefuel(fleet))
                {
                    var (fuel, _) = ship.GetFuelInfo(cargoLibrary);
                    if (fuel == null)
                        continue;
                    long units = CargoMath.GetUnitsStored(colonyStore, fuel, includeEscro: false);
                    if (units <= 0)
                        return $"colony has 0 {fuel.UniqueID} (ships need it)";
                }
            }
            catch
            {
                // fall through
            }

            return "no lasting transfers (see CreateRefuelFleetCommand log)";
        }

        internal override bool IsValidCommand(Game game)
        {
            if (!CommandHelpers.IsCommandValid(
                    game.GlobalManager,
                    RequestingFactionGuid,
                    EntityCommandingGuid,
                    out _,
                    out _entityCommanding))
                return false;

            return _colony != null && _colony.HasDataBlob<ColonyInfoDB>();
        }

        public override EntityCommand Clone()
        {
            return new RefuelWhenAtColonyOrder
            {
                UseActionLanes = UseActionLanes,
                RequestingFactionGuid = RequestingFactionGuid,
                EntityCommandingGuid = EntityCommandingGuid,
                CreatedDate = CreatedDate,
                ActionOnDate = ActionOnDate,
                ActionedOnDate = ActionedOnDate,
                _entityCommanding = _entityCommanding,
                _colony = _colony,
            };
        }

        internal override void BindCommandingEntity(Entity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            _entityCommanding = entity;
            base.BindCommandingEntity(entity);
        }
    }
}
