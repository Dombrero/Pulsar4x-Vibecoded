using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Pulsar4X.Orbital;
using Pulsar4X.Extensions;
using Pulsar4X.Colonies;
using Pulsar4X.Energy;
using Pulsar4X.Fleets;
using Pulsar4X.Names;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Galaxy;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Engine;
using Pulsar4X.Datablobs;
using Pulsar4X.Api;
using Stringify = Pulsar4X.Api.Stringify;

namespace Pulsar4X.Movement
{
    public class WarpMoveCommand : EntityCommand
    {

        public override string Name
        {
            get
            {
                if (!_targetEntity.IsValid || !_entityCommanding.IsValid)
                    return "Warp Move";

                return "Warp Move to " + _targetEntity.GetName(_entityCommanding.FactionOwnerID);
            }
        }

        public override string Details
        {
            get
            {
                string targetName = _targetEntity.GetDataBlob<NameDB>().GetName(_factionEntity);
                double offset_m = EndpointRelitivePosition.Length();
                double travel_m = 0;
                if (_warpingDB != null)
                    travel_m = (_warpingDB.ExitPointAbsolute - _warpingDB.EntryPointAbsolute).Length();
                else if (_entityCommanding.IsValid && _targetEntity.IsValid)
                {
                    try
                    {
                        travel_m = (MoveMath.GetAbsoluteState(_targetEntity).pos
                                    - MoveMath.GetAbsoluteState(_entityCommanding).pos).Length();
                    }
                    catch { /* best-effort for UI */ }
                }

                // Offset is often 0 for grav anomalies (park on the point) — show travel distance.
                double shown = travel_m > 1 ? travel_m : offset_m;
                return "Warp to + " + Stringify.Distance(shown) + " from " + targetName;
            }
        }

        public override ActionLaneTypes ActionLanes => ActionLaneTypes.Movement;
        public override bool IsBlocking => true;

        [JsonProperty]
        public int TargetEntityGuid { get; set; }

        private Entity _targetEntity = Entity.InvalidEntity;


        [JsonIgnore]
        Entity _factionEntity = Entity.InvalidEntity;
        WarpMovingDB? _warpingDB;


        Entity _entityCommanding = Entity.InvalidEntity;
        internal override Entity EntityCommanding { get { return _entityCommanding; } }

        public DateTime TransitStartDateTime;
        public Vector3 EndpointRelitivePosition { get; set; }
        public Vector3 EndpointTargetExpendDeltaV;
        /// <summary>
        /// the orbit we want to be in at the target.
        /// </summary>
        public KeplerElements EndpointTargetOrbit;
        public static bool CreateCommand(
            Entity orderEntity,
            Entity targetEntity,
            DateTime transitStartDatetime,
            Vector3 endpointRelativePos = new Vector3())
        {
            var datetimeArrive = WarpMath.GetInterceptPosition(orderEntity, targetEntity, transitStartDatetime, endpointRelativePos);

            var cmd = new WarpMoveCommand()
            {
                RequestingFactionGuid = orderEntity.FactionOwnerID,
                EntityCommandingGuid = orderEntity.Id,
                CreatedDate = orderEntity.AttachedManager.ManagerSubpulses.StarSysDateTime,
                TargetEntityGuid = targetEntity.Id,
                EndpointRelitivePosition = endpointRelativePos,
                TransitStartDateTime = transitStartDatetime,
            };
            if (targetEntity.GetDataBlob<PositionDB>().MoveType != PositionDB.MoveTypes.None)
            {
                var sgp = GeneralMath.StandardGravitationalParameter(targetEntity.GetDataBlob<MassVolumeDB>().MassTotal + orderEntity.GetDataBlob<MassVolumeDB>().MassTotal);
                cmd.EndpointTargetOrbit = OrbitMath.KeplerCircularFromPosition(sgp, endpointRelativePos, datetimeArrive.Item2); ;
            }
            return OrderEnqueue.Enqueue(orderEntity.AttachedManager.Game, cmd);
        }

        /// <summary>
        /// Creates a warp order with an attempted simplenewt circular orbit post warp.
        /// DOES NOT QUEUE THE COMMAND. OrderEnqueue.Enqueue(game, cmd) should be called
        /// </summary>
        /// <param name="orderEntity"></param>
        /// <param name="targetEntity"></param>
        /// <param name="transitStartDatetime"></param>
        /// <returns></returns>
        public static WarpMoveCommand CreateCommandEZ(
            Entity orderEntity,
            Entity targetEntity,
            DateTime transitStartDatetime)
        {
            //if target is a colony, just make the target the parent planet.
            if (targetEntity.TryGetDataBlob<ColonyInfoDB>(out var colonyInfo) && colonyInfo is not null)
                targetEntity = colonyInfo.PlanetEntity;

            (Vector3 pos, Vector3 vel) departureState;
            try
            {
                if (orderEntity.AttachedManager.Game.Settings.UseRelativeVelocity)
                    departureState = MoveMath.GetRelativeFutureState(orderEntity, transitStartDatetime);
                else
                    departureState = MoveMath.GetAbsoluteState(orderEntity, transitStartDatetime);
            }
            catch
            {
                // Ship may lack OrbitDB after an aborted warp; still allow a new warp order.
                var abs = orderEntity.TryGetDataBlob<PositionDB>(out var pdb)
                    ? pdb.AbsolutePosition
                    : Vector3.Zero;
                departureState = (abs, new Vector3(0, 1, 0));
            }

            if (departureState.vel.Length() < 1e-9)
                departureState.vel = new Vector3(0, 1, 0);

            var cmd = new WarpMoveCommand()
            {
                RequestingFactionGuid = orderEntity.FactionOwnerID,
                EntityCommandingGuid = orderEntity.Id,
                CreatedDate = orderEntity.AttachedManager.ManagerSubpulses.StarSysDateTime,
                TargetEntityGuid = targetEntity.Id,
                TransitStartDateTime = transitStartDatetime,

            };

            switch (targetEntity.GetDataBlob<PositionDB>().MoveType) //if the targetEntity's movetype is this:
            {
                case PositionDB.MoveTypes.None: //this means it's a grav anomaly, jump point
                    {
                        break;
                    }
                case PositionDB.MoveTypes.Orbit:
                    {
                        var sgp = OrbitMath.SGP(targetEntity, orderEntity);
                        var lowOrbitRadius = OrbitMath.LowOrbitRadius(targetEntity);
                        var perpVec = Vector3.Normalise(new Vector3(departureState.vel.Y * -1, departureState.vel.X, 0));
                        var lowOrbitPos = perpVec * lowOrbitRadius;
                        (Vector3 pos, DateTime eti) targetIntercept = WarpMath.GetInterceptPosition(orderEntity, targetEntity, transitStartDatetime, lowOrbitPos);
                        var lowOrbit = OrbitMath.KeplerCircularFromPosition(sgp, lowOrbitPos, targetIntercept.eti);
                        var lowOrbitState = OrbitMath.GetStateVectors(lowOrbit, targetIntercept.eti);
                        var targetEntityOrbitDb = targetEntity.GetDataBlob<OrbitDB>();
                        Vector3 insertionVector = OrbitProcessor.GetOrbitalInsertionVector(departureState.vel, targetEntityOrbitDb, targetIntercept.eti);
                        var deltaV = insertionVector - (Vector3)lowOrbitState.velocity;

                        cmd.EndpointRelitivePosition = lowOrbitPos;
                        cmd.EndpointTargetOrbit = lowOrbit;
                        cmd.EndpointTargetExpendDeltaV = deltaV;
                        break;
                    }
                case PositionDB.MoveTypes.NewtonSimple:
                    {
                        //recursive call here, if the target we're trying to go to is manuvering somewhere,
                        //then just target that targets target...
                        //TODO we should check if the target is another empire, in such case we probilby shouldn't know the target?
                        //but maybe we can guess it. idk.
                        var wp = targetEntity.GetDataBlob<WarpMovingDB>();
                        if (wp.TargetEntity is not { IsValid: true } nestedTarget)
                            throw new InvalidOperationException("Warp target has no valid nested target.");
                        cmd = CreateCommandEZ(orderEntity, nestedTarget, transitStartDatetime);
                        break;
                    }
                case PositionDB.MoveTypes.NewtonComplex:
                    {
                        //recursive call here, if the target we're trying to go to is manuvering somewhere,
                        //then just target that targets target...
                        //TODO we should check if the target is another empire, in such case we probilby shouldn't know the target?
                        //but maybe we can guess it. idk.
                        var wp = targetEntity.GetDataBlob<WarpMovingDB>();
                        if (wp.TargetEntity is not { IsValid: true } nestedTarget)
                            throw new InvalidOperationException("Warp target has no valid nested target.");
                        cmd = CreateCommandEZ(orderEntity, nestedTarget, transitStartDatetime);
                        break;
                    }
                case PositionDB.MoveTypes.Warp:
                    {
                        //recursive call here, if the target we're trying to go to is warping somewhere,
                        //then just target that targets target...
                        //TODO we should check if the target is another empire, in such case we probilby shouldn't know the target?
                        //but maybe we can guess it. idk.
                        var wp = targetEntity.GetDataBlob<WarpMovingDB>();
                        if (wp.TargetEntity is not { IsValid: true } nestedTarget)
                            throw new InvalidOperationException("Warp target has no valid nested target.");
                        cmd = CreateCommandEZ(orderEntity, nestedTarget, transitStartDatetime);
                        break;
                    }
                default:
                    throw new NotImplementedException();
            }

            //OrderEnqueue.Enqueue(orderEntity.AttachedManager.Game, cmd);


            return cmd;
        }

        internal override bool IsValidCommand(Game game)
        {
            if (CommandHelpers.IsCommandValid(game.GlobalManager, RequestingFactionGuid, EntityCommandingGuid, out _factionEntity, out _entityCommanding))
            {
                if (game.GlobalManager.TryGetGlobalEntityById(TargetEntityGuid, out _targetEntity))
                {
                    return true;
                }
            }
            return false;
        }

        internal override void Execute(DateTime atDateTime)
        {
            if (!IsRunning)
            {
                if (!_entityCommanding.TryGetDataBlob<WarpAbilityDB>(out var warpDB)
                    || !_entityCommanding.TryGetDataBlob<EnergyGenAbilityDB>(out var powerDB))
                {
                    DebugTraceLog.Warn("Warp",
                        $"ship#{_entityCommanding.Id}: warp blocked — missing WarpAbility/EnergyGen",
                        atDateTime);
                    return;
                }

                // Capacitors start at 0 and only fill when EnergyGen runs. Checking before
                // generating left warps queued forever ("en route") with full cargo fuel tanks.
                try
                {
                    EnergyGenProcessor.EnergyGen(_entityCommanding, atDateTime);
                }
                catch (Exception ex)
                {
                    DebugTraceLog.Warn("Warp",
                        $"ship#{_entityCommanding.Id}: EnergyGen failed: {ex.Message}",
                        atDateTime);
                    return;
                }

                string eType = warpDB.EnergyType;
                if (string.IsNullOrEmpty(eType)
                    || !powerDB.EnergyStored.TryGetValue(eType, out double estored))
                {
                    DebugTraceLog.Warn("Warp",
                        $"ship#{_entityCommanding.Id}: warp blocked — no energy store for '{eType}'",
                        atDateTime);
                    return;
                }

                if (!WarpMoveProcessor.HasWarpTankFuel(_entityCommanding))
                {
                    DebugTraceLog.Warn("Warp",
                        $"ship#{_entityCommanding.Id}: warp blocked — cargo fuel tank empty",
                        atDateTime);
                    return;
                }

                _warpingDB = new WarpMovingDB(_entityCommanding, _targetEntity, EndpointRelitivePosition, EndpointTargetOrbit);

                //if we're already in a warp moving state,
                //then we should carry over the SavedNewtonionVector.
                //this will happen in the case of serveying grav anomalies.
                if (_entityCommanding.TryGetDataBlob<WarpMovingDB>(out var warpMovingDB))
                {
                    _warpingDB.SavedNewtonionVector = warpMovingDB.SavedNewtonionVector;
                }

                EntityCommanding.SetDataBlob(_warpingDB);

                double distanceM = (_warpingDB.ExitPointAbsolute
                    - _entityCommanding.GetDataBlob<PositionDB>().AbsolutePosition).Length();

                if (!WarpMoveProcessor.TryGetWarpEnergyNeed(
                        warpDB, powerDB, distanceM,
                        out double needKJ, out double travelSeconds, out double creationCost, out double sustainDeficit)
                    || double.IsInfinity(travelSeconds))
                {
                    _entityCommanding.RemoveDataBlob<WarpMovingDB>();
                    _warpingDB = null;
                    DebugTraceLog.Warn("Warp",
                        $"ship#{_entityCommanding.Id}: warp blocked — invalid hop (speed={warpDB.MaxSpeed}, dist={distanceM:0}m)",
                        atDateTime);
                    return;
                }

                if (needKJ > estored)
                {
                    // Catch up generation across large time-steps so one daily tick can fill batteries.
                    CatchUpEnergyStore(_entityCommanding, powerDB, eType, needKJ, atDateTime);
                    estored = powerDB.EnergyStored[eType];
                }

                if (needKJ > estored)
                {
                    _entityCommanding.RemoveDataBlob<WarpMovingDB>();
                    _warpingDB = null;
                    DebugTraceLog.Warn("Warp",
                        $"ship#{_entityCommanding.Id}: warp blocked — need {needKJ:0} kJ " +
                        $"(creation {creationCost:0} + sustain deficit {sustainDeficit:0} over {travelSeconds:0}s), " +
                        $"have {estored:0} kJ (cargo fuel is separate)",
                        atDateTime);
                    return;
                }

                if (!WarpMoveProcessor.StartNonNewtTranslation(EntityCommanding))
                {
                    // Do not leave a half-started WarpMovingDB on the ship.
                    if (_entityCommanding.HasDataBlob<WarpMovingDB>())
                        _entityCommanding.RemoveDataBlob<WarpMovingDB>();
                    _warpingDB = null;
                    powerDB.EnergyStored.TryGetValue(eType, out estored);
                    DebugTraceLog.Warn("Warp",
                        $"ship#{_entityCommanding.Id}: warp start failed — need {needKJ:0} kJ, have {estored:0} kJ " +
                        $"(or tank fuel missing after check)",
                        atDateTime);
                    return;
                }

                IsRunning = true;
            }
        }

        /// <summary>
        /// EnergyGen applies ~1s of output per call. Large pulses must catch up or warps never start.
        /// </summary>
        private static void CatchUpEnergyStore(
            Entity ship, EnergyGenAbilityDB powerDB, string eType, double need, DateTime atDateTime)
        {
            const int maxSteps = 10_000;
            for (int i = 0; i < maxSteps; i++)
            {
                if (!powerDB.EnergyStored.TryGetValue(eType, out double stored))
                    return;
                if (stored >= need)
                    return;
                if (powerDB.EnergyStoreMax.TryGetValue(eType, out double max) && stored >= max - 1e-6)
                    return;
                try
                {
                    EnergyGenProcessor.EnergyGen(ship, atDateTime);
                }
                catch
                {
                    return;
                }
            }
        }

        internal override bool IsFinished()
        {
            if (WasCancelled)
                return _isFinished = true;
            if (_warpingDB != null)
                _isFinished = _warpingDB.IsAtTarget;
            else
                _isFinished = false;
            return _isFinished;
        }

        /// <summary>True when aborted to make room for a higher-priority move.</summary>
        internal bool WasCancelled { get; private set; }

        /// <summary>Mark cancelled so fleet-level move orders can re-dispatch after a preempt.</summary>
        internal void CancelInPlace()
        {
            WasCancelled = true;
            _isFinished = true;
            IsRunning = false;
            _warpingDB = null;
        }

        public override EntityCommand Clone()
        {
            throw new NotImplementedException();
        }
    }

    public class WarpFleetTowardsTargetOrder : EntityCommand
    {
        public override ActionLaneTypes ActionLanes => ActionLaneTypes.Movement;

        public override bool IsBlocking => true;

        public override string Name => "Move Fleet Towards Target";

        public override string Details => "";

        private Entity _entityCommanding = Entity.InvalidEntity;

        internal override Entity EntityCommanding => _entityCommanding;

        public Entity Target { get; set; } = Entity.InvalidEntity;
        List<WarpMoveCommand> _shipCommands = new List<WarpMoveCommand>();

        public override EntityCommand Clone()
        {
            throw new NotImplementedException();
        }

        internal override bool IsFinished()
        {
            if (!IsRunning)
                return _isFinished = false;

            // Ship warps were preempted — stay in the fleet queue and re-dispatch later.
            if (_shipCommands.Any(c => c.WasCancelled))
                return _isFinished = false;

            // Empty after Execute: every ship was already at the target (or colony body).
            if (_shipCommands.Count == 0)
                return _isFinished = true;

            foreach (var command in _shipCommands)
            {
                if (!command.IsFinished())
                    return _isFinished = false;
            }
            return _isFinished = true;
        }

        internal override void Execute(DateTime atDateTime)
        {
            if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB)) return;

            bool needsRedispatch = !IsRunning
                || _shipCommands.Count == 0
                || _shipCommands.Any(c => c.WasCancelled)
                || _shipCommands.All(c =>
                    !c.EntityCommanding.TryGetDataBlob<OrderableDB>(out var shipOrders)
                    || !shipOrders.ActionList.Contains(c));

            if (IsRunning && !needsRedispatch)
                return;

            // Clear leftover ship Movement-lane warps so the new destination runs now.
            FleetOrderCleanup.AbortShipMovementOrders(_entityCommanding);

            _shipCommands.Clear();
            var ships = fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>());

            foreach (var ship in ships)
            {
                var shipParent = ship.GetDataBlob<PositionDB>().Parent;
                if (shipParent == Target)
                    continue;
                if (Target.TryGetDataBlob<ColonyInfoDB>(out var colonyDB) && colonyDB.PlanetEntity == shipParent)
                    continue;
                if (!ship.HasDataBlob<WarpAbilityDB>()) continue;

                try
                {
                    var shipCommand = WarpMoveCommand.CreateCommandEZ(ship, Target, atDateTime);
                    _shipCommands.Add(shipCommand);
                    OrderEnqueue.Enqueue(ship.AttachedManager.Game, shipCommand);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WarpFleetTowardsTarget ship {ship.Id}: {ex.Message}");
                }
            }
            IsRunning = true;
        }

        public static WarpFleetTowardsTargetOrder CreateCommand(Entity fleet, Entity target)
        {
            var order = new WarpFleetTowardsTargetOrder()
            {
                RequestingFactionGuid = fleet.FactionOwnerID,
                EntityCommandingGuid = fleet.Id,
                _entityCommanding = fleet,
                Target = target,
            };

            return order;
        }

        internal override bool IsValidCommand(Game game)
        {
            return true;
        }
    }
}
