using System;
using Pulsar4X.Api;
using Pulsar4X.Orbital;
using Pulsar4X.Datablobs;
using Pulsar4X.Interfaces;
using Pulsar4X.Extensions;
using Pulsar4X.Energy;
using Pulsar4X.Factions;
using Pulsar4X.Orbits;
using Pulsar4X.Galaxy;
using Pulsar4X.Engine;
using Pulsar4X.JumpPoints;
using Pulsar4X.Storage;

namespace Pulsar4X.Movement
{
    /// <summary>
    /// Translate move processor.
    ///
    ///
    /// Non Newtonion Movement/Translation
    /// Rules:
    /// (Eventualy)
    /// An entry point and an exit point for translation is defined.
    /// Ships newtonion velocity is stored at the translation entry point.
    /// Ship enters a non newtonion translation state
    /// in this state, the ship is unaffected by it's previous newtonion vector & gravity
    /// Acceleration is instant.
    /// Speed is shown relative to the parent star.
    /// Cannot change its direction or speed untill exit.**
    /// An exit should be able to be forced prematurly, but this should come at a cost.
    /// An exit should be able to be forced by outside (enemy) forces. *
    /// Possibly the cost should be handeled by having entering the translation state
    ///     be expensive, while the travel distance/speed is ralitivly cheap.
    ///
    /// On Exit, the saved newtonion vector is given back to the ship
    ///   if the exit point and velocity does not give the required orbit
    ///   then DeltaV (normal newtonion movement) will be expended to get to that orbit.
    ///
    /// Cost of translation TBD, either special fuel and/or energy requiring reactor fuel + capacitors/batteries
    /// Exit position accuracy should be a factor of tech and skill.
    /// Max Speed should be a factor of engine power and mass of the ship. (as it is currently)
    ///   Engine Power should be a factor of engine size/design etc and tech.
    /// Cost should be a factor of tech. (& maybe skill to a small degree?)
    ///
    /// *(todo think of gameplay mechanic, anti ftl missiles?
    ///   I feel that normal combat shouldn't take place within translation state,
    ///   but this could make combat difficult to code).
    ///
    ///
    /// I considered tying the non-newtonion speed vector to actual still space,
    /// but finding how fast the sun is actualy moving proved difficult,
    /// many websites just added speeds of galaxy + solarsystem together and ignored the relative vectors.
    /// one site I found sugested 368 ± 2 km/s
    /// this might not be terrible, however if we gave max speeds of that number,
    /// we'd be able to travel 368 km/s in one direction, and none in the oposite direction.
    /// so we'd need to give max speeds of more than that, and/or force homman transfers in one direction.
    /// could provide an interesting gameplay mechanic...
    ///
    /// **
    ///NB I've alowed ships to come to zero speed warp when serveying a jump point grav anomaly, since these are still in space.
    /// this may cause some problems we will have to see how it plays out.
    /// </summary>
    public class WarpMoveProcessor : IHotloopProcessor
    {
        public TimeSpan RunFrequency => TimeSpan.FromMinutes(5);

        public TimeSpan FirstRunOffset => TimeSpan.FromMinutes(0);

        public Type GetParameterType => typeof(WarpMovingDB);

        public void Init(Game game)
        {
        }



        public int ProcessManager(EntityManager manager, int deltaSeconds)
        {
            var datablobs = manager.GetAllDataBlobsOfType<WarpMovingDB>();
            DateTime todateTime = manager.StarSysDateTime + TimeSpan.FromSeconds(deltaSeconds);
            foreach (var db in datablobs)
            {
                // EndWarpMove may RemoveDataBlob mid-loop → OwningEntity becomes InvalidEntity.
                if (db.OwningEntity is not { Manager: not null, IsValid: true } owner)
                    continue;
                WarpMove(owner, db, todateTime);
            }
            // Skips blobs whose OwningEntity was cleared by arrival this tick.
            MoveStateProcessor.ProcessForType(datablobs, todateTime);
            return datablobs.Count;
        }



        /// <summary>
        /// Moves an entity while it's in a non newtonion translation state.
        /// </summary>
        /// <param name="entity">Entity.</param>
        /// <param name="deltaSeconds">Unused</param>
        public void ProcessEntity(Entity entity, int deltaSeconds)
        {
            var db = entity.GetDataBlob<WarpMovingDB>();
            DateTime toDateTime = entity.StarSysDateTime + TimeSpan.FromSeconds(deltaSeconds);
            WarpMove(entity, db, toDateTime);
            // Arrival may have removed WarpMovingDB; InvalidEntity is not null.
            if (db.OwningEntity is { Manager: not null, IsValid: true })
                MoveStateProcessor.ProcessForType(db, toDateTime);
        }

        public static void ProcessEntity(Entity entity, DateTime toDateTime)
        {
            var db = entity.GetDataBlob<WarpMovingDB>();
            WarpMove(entity, db, toDateTime);
            if (db.OwningEntity is { Manager: not null, IsValid: true })
                MoveStateProcessor.ProcessForType(db, toDateTime);
        }

        public static void WarpMove(Entity entity, WarpMovingDB moveDB, DateTime toDateTime)
        {
            if (moveDB.HasStarted || StartNonNewtTranslation(entity))
            {
                var warpDB = entity.GetDataBlob<WarpAbilityDB>();

                var currentVelocityMS = moveDB.CurrentNonNewtonionVectorMS;
                DateTime dateTimeFrom = moveDB.LastProcessDateTime;

                double deltaT = (toDateTime - dateTimeFrom).TotalSeconds;

                Vector3 targetPosMt = moveDB.ExitPointAbsolute;

                var newPositionMt = moveDB._position + (Vector2)currentVelocityMS * deltaT;

                double distanceToMove = (moveDB._position - newPositionMt).Length();
                double distanceToTargetMt = (moveDB._position - (Vector2)targetPosMt).Length();

                if (distanceToTargetMt <= distanceToMove) // moving would overtake target, just go directly to target
                {
                    // Grav anomalies / jump points stay in WarpMovingDB as a zero-speed hover.
                    // Without this guard every later tick re-enters arrival and re-bills tank fuel
                    // (same hop burned ~N times until empty — see Fuel log spam).
                    if (moveDB.IsAtTarget)
                    {
                        moveDB.LastProcessDateTime = toDateTime;
                        return;
                    }

                    if (moveDB.TargetEntity is not { IsValid: true } targetEntity)
                    {
                        EndWarpMove(entity, warpDB, moveDB, toDateTime);
                        return;
                    }
                    moveDB._parentEnitity = targetEntity;
                    moveDB._position = (Vector2)moveDB.ExitPointrelative;
                    var destinationMoveType = targetEntity.GetDataBlob<PositionDB>().MoveType;
                    moveDB.IsAtTarget = true;
                    //if our destination is a non moving object eg a grav anomaly or jump point.
                    if (destinationMoveType == PositionDB.MoveTypes.None)
                    {
                        moveDB.CurrentNonNewtonionVectorMS = Vector3.Zero;
                        // Stay in zero-speed warp (design), but sync PositionDB + bill tank fuel once.
                        FinishWarpAtStaticTarget(entity, warpDB, moveDB, toDateTime);
                    }
                    else
                        EndWarpMove(entity, warpDB, moveDB, toDateTime);
                }
                else
                {
                    moveDB._position = newPositionMt;
                }


                moveDB.LastProcessDateTime = toDateTime;
            }
        }

        /// <summary>
        /// Battery kJ needed to start a warp hop of <paramref name="distanceM"/>.
        /// Creation is a one-shot pulse; sustain is continuous demand covered by reactor output first,
        /// with only the deficit drawn from batteries over the trip.
        /// </summary>
        public static bool TryGetWarpEnergyNeed(
            WarpAbilityDB warpDB,
            EnergyGenAbilityDB powerDB,
            double distanceM,
            out double needKJ,
            out double travelSeconds,
            out double creationCost,
            out double sustainDeficitKJ)
        {
            needKJ = 0;
            travelSeconds = double.PositiveInfinity;
            creationCost = warpDB.BubbleCreationCost;
            sustainDeficitKJ = 0;

            if (warpDB.MaxSpeed <= 0 || double.IsNaN(distanceM) || distanceM < 0)
                return false;

            travelSeconds = distanceM / warpDB.MaxSpeed;
            if (double.IsInfinity(travelSeconds) || double.IsNaN(travelSeconds))
                return false;

            double sustainDeficitKW = Math.Max(0, warpDB.BubbleSustainCost - powerDB.TotalOutputMax);
            sustainDeficitKJ = sustainDeficitKW * travelSeconds;
            needKJ = creationCost + sustainDeficitKJ;
            return true;
        }

        /// <summary>
        /// Heliocentric frame used while warping: primary star / gravity root.
        /// Grav anomalies and jump points are MoveTypes.None with no parent, so
        /// TreeHierarchyDB.Root is the anomaly itself — parenting a ship
        /// there and then writing heliocentric AbsolutePosition as RelativePosition
        /// double-offsets and looks like a teleport.
        /// </summary>
        internal static Entity? GetSystemWarpFrame(Entity entity)
        {
            var manager = entity.AttachedManager;
            if (manager == null)
                return null;

            try
            {
                var star = manager.GetFirstEntityWithDataBlob<StarInfoDB>();
                if (star == null || !star.IsValid)
                    return null;

                if (star.TryGetDataBlob<OrbitDB>(out var orbit)
                    && orbit.Root is { IsValid: true } gravityRoot
                    && gravityRoot.HasDataBlob<PositionDB>())
                    return gravityRoot;

                if (star.HasDataBlob<PositionDB>())
                    return star;
            }
            catch
            {
                // Fall through — caller may use PositionDB.Root / null.
            }

            return null;
        }

        /// <summary>
        /// Reparent into the heliocentric warp frame while preserving AbsolutePosition.
        /// </summary>
        internal static void AttachToSystemWarpFrame(Entity entity, PositionDB positionDB)
        {
            Vector3 absolute = positionDB.AbsolutePosition;
            var frame = GetSystemWarpFrame(entity);

            if (frame != null && frame.IsValid && frame != entity)
            {
                if (positionDB.Parent != frame)
                    positionDB.SetParent(frame);
            }
            else if (positionDB.Parent != null)
            {
                positionDB.SetParent(null);
            }

            // SetParent preserves absolute; re-assert in case parent was already frame
            // but RelativePosition was stale from a previous double-offset.
            positionDB.AbsolutePosition = absolute;
        }

        public static bool StartNonNewtTranslation(Entity entity)
        {
            var warpDB = entity.GetDataBlob<WarpAbilityDB>();
            var positionDB = entity.GetDataBlob<PositionDB>();
            var maxSpeedMS = warpDB.MaxSpeed;
            var powerDB = entity.GetDataBlob<EnergyGenAbilityDB>();
            EnergyGenProcessor.EnergyGen(entity, entity.StarSysDateTime);

            var moveDB = entity.GetDataBlob<WarpMovingDB>();
            Vector3 currentPositionMt = positionDB.AbsolutePosition;
            moveDB._position = (Vector2)positionDB.AbsolutePosition;
            Vector3 targetPosMt = moveDB.ExitPointAbsolute;
            double totalDistance = (currentPositionMt - targetPosMt).Length();

            if (!TryGetWarpEnergyNeed(warpDB, powerDB, totalDistance,
                    out double needKJ, out double travelSeconds, out _, out _)
                || !powerDB.EnergyStored.TryGetValue(warpDB.EnergyType, out double estored)
                || needKJ > estored
                || !HasWarpTankFuel(entity))
            {
                return false;
            }

            // Detach into heliocentric frame once the hop is affordable.
            AttachToSystemWarpFrame(entity, positionDB);

            // Keep MoveState parent in sync — otherwise ProcessForType nulls Parent via unset _parentEnitity.
            moveDB._parentEnitity = positionDB.Parent is { IsValid: true } p
                ? p
                : (GetSystemWarpFrame(entity) ?? moveDB._parentEnitity);
            // Transit positions are heliocentric (matched against ExitPointAbsolute).
            moveDB._position = (Vector2)positionDB.AbsolutePosition;
            targetPosMt = moveDB.ExitPointAbsolute;

            var currentVelocityMS = Vector3.Normalise(targetPosMt - positionDB.AbsolutePosition) * maxSpeedMS;
            moveDB.CurrentNonNewtonionVectorMS = currentVelocityMS;
            moveDB.LastProcessDateTime = entity.StarSysDateTime;

            powerDB.AddDemand(warpDB.BubbleCreationCost, entity.StarSysDateTime);
            powerDB.AddDemand(-warpDB.BubbleCreationCost, entity.StarSysDateTime + TimeSpan.FromSeconds(1));
            powerDB.AddDemand(warpDB.BubbleSustainCost, entity.StarSysDateTime + TimeSpan.FromSeconds(1));
            moveDB.HasStarted = true;
            return true;
        }


        /// <summary>
        /// Arrive at a MoveTypes.None target (grav anomaly / jump point): keep zero-speed warp
        /// (ships "hover" on warp resources) but sync PositionDB and consume tank fuel.
        /// Stay in the heliocentric frame — do not parent the ship to the anomaly.
        /// </summary>
        static void FinishWarpAtStaticTarget(Entity entity, WarpAbilityDB warpDB, WarpMovingDB moveDB, DateTime toDateTime)
        {
            if (moveDB.TargetEntity == null)
                return;

            double hopLen = (moveDB.ExitPointAbsolute - moveDB.EntryPointAbsolute).Length();
            MovementStuckWatchdog.NoteWarpHopCompleted(entity, moveDB.TargetEntity, hopLen, toDateTime);

            ConsumeWarpTankFuel(entity, moveDB, toDateTime);

            if (entity.TryGetDataBlob<PositionDB>(out var pos))
            {
                pos.AbsolutePosition = moveDB.ExitPointAbsolute;
                AttachToSystemWarpFrame(entity, pos);
                pos.AbsolutePosition = moveDB.ExitPointAbsolute;
                pos.MoveType = PositionDB.MoveTypes.Warp;
            }

            var frame = GetSystemWarpFrame(entity);
            moveDB._parentEnitity = frame is { IsValid: true } f ? f : moveDB.TargetEntity;
            // MoveStateProcessor writes _position as RelativePosition under _parentEnitity —
            // must be relative to the frame, not absolute (absolute-as-relative teleports off-gate).
            if (frame is { IsValid: true }
                && frame.TryGetDataBlob<PositionDB>(out var framePos))
            {
                moveDB._position = (Vector2)(moveDB.ExitPointAbsolute - framePos.AbsolutePosition);
            }
            else
            {
                moveDB._position = (Vector2)moveDB.ExitPointAbsolute;
            }
            moveDB.LastProcessDateTime = toDateTime;

            var powerDB = entity.GetDataBlob<EnergyGenAbilityDB>();
            powerDB.AddDemand(warpDB.BubbleCollapseCost, entity.StarSysDateTime);
            powerDB.AddDemand(-warpDB.BubbleSustainCost, entity.StarSysDateTime);
            powerDB.AddDemand(-warpDB.BubbleCollapseCost, entity.StarSysDateTime + TimeSpan.FromSeconds(1));
            // Sustain zero-speed hover at the anomaly.
            powerDB.AddDemand(warpDB.BubbleSustainCost, entity.StarSysDateTime + TimeSpan.FromSeconds(1));

            // Jump-gate arrivals: wake Orderable so ShipJump (non-blocking) can transit this tick
            // instead of waiting behind a finished WarpMoveCommand on the Movement lane.
            if (moveDB.TargetEntity is { IsValid: true } tgt && tgt.HasDataBlob<JumpPointDB>())
            {
                try
                {
                    var game = entity.AttachedManager?.Game;
                    game?.ProcessorManager
                        .GetInstanceProcessor(nameof(OrderableProcessor))
                        .ProcessEntity(entity, toDateTime);
                }
                catch
                {
                    // Next Orderable hotloop will retry.
                }
            }
        }

        static void EndWarpMove(Entity entity, WarpAbilityDB warpDB, WarpMovingDB moveDB, DateTime toDateTime)
        {
            var powerDB = entity.GetDataBlob<EnergyGenAbilityDB>();



            powerDB.AddDemand(warpDB.BubbleCollapseCost, entity.StarSysDateTime);
            powerDB.AddDemand(-warpDB.BubbleSustainCost, entity.StarSysDateTime);
            powerDB.AddDemand(-warpDB.BubbleCollapseCost, entity.StarSysDateTime + TimeSpan.FromSeconds(1));

            if (moveDB.TargetEntity is not { IsValid: true } targetEntity)
            {
                FinishWarpAtStaticTarget(entity, warpDB, moveDB, toDateTime);
                return;
            }
            var destinationMoveType = targetEntity.GetDataBlob<PositionDB>().MoveType;

            switch (destinationMoveType)
            {
                case PositionDB.MoveTypes.None:
                    {
                        FinishWarpAtStaticTarget(entity, warpDB, moveDB, toDateTime);
                        break;
                    }
                case PositionDB.MoveTypes.Orbit:
                    {
                        // Predictable tank drain for the hop (not uncapped newton circularisation).
                        ConsumeWarpTankFuel(entity, moveDB, toDateTime);
                        entity.RemoveDataBlob<WarpMovingDB>();
                        try
                        {
                            SetOrbitHereNoNewt(entity, moveDB, toDateTime);
                        }
                        catch (Exception ex)
                        {
                            // Arrival must not tear down the time loop (looks like spontaneous pause).
                            DebugTraceLog.Error("Warp",
                                $"ship#{entity.Id}: orbit arrival failed: {ex.GetType().Name}: {ex.Message}",
                                toDateTime);
                            moveDB.IsAtTarget = true;
                        }
                        break;
                    }
                case PositionDB.MoveTypes.NewtonSimple:
                case PositionDB.MoveTypes.NewtonComplex:
                    {
                        // Not implemented yet — park on a circular orbit instead of crashing Play.
                        DebugTraceLog.Warn("Warp",
                            $"ship#{entity.Id}: warp exit MoveType={destinationMoveType} unsupported; using circular orbit",
                            toDateTime);
                        ConsumeWarpTankFuel(entity, moveDB, toDateTime);
                        entity.RemoveDataBlob<WarpMovingDB>();
                        try
                        {
                            SetOrbitHereNoNewt(entity, moveDB, toDateTime);
                        }
                        catch (Exception ex)
                        {
                            DebugTraceLog.Error("Warp",
                                $"ship#{entity.Id}: fallback orbit arrival failed: {ex.GetType().Name}: {ex.Message}",
                                toDateTime);
                            moveDB.IsAtTarget = true;
                        }
                        break;
                    }
                case PositionDB.MoveTypes.Warp:
                    {
                        var targetSpeed = moveDB.TargetEntity.GetDataBlob<WarpMovingDB>().CurrentNonNewtonionVectorMS;
                        var newspeed = Math.Min(targetSpeed.Length(), warpDB.MaxSpeed);
                        moveDB.CurrentNonNewtonionVectorMS = Vector3.Normalise(targetSpeed) * newspeed;
                        break;
                    }
                default:
                    DebugTraceLog.Warn("Warp",
                        $"ship#{entity.Id}: unknown warp exit MoveType={destinationMoveType}; treating as static",
                        toDateTime);
                    FinishWarpAtStaticTarget(entity, warpDB, moveDB, toDateTime);
                    break;
            }

        }


        /// <summary>
        /// True when the ship has no cargo fuel type, or at least a little fuel left to warp.
        /// </summary>
        internal static bool HasWarpTankFuel(Entity entity)
        {
            try
            {
                if (!entity.TryGetDataBlob<CargoStorageDB>(out var storage))
                    return true;

                var cargoLib = entity.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                var (fuel, _) = entity.GetFuelInfo(cargoLib);
                if (fuel == null)
                    return true;

                return storage.GetUnitsStored(fuel, includeEscro: false) > 0;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Tank units that <see cref="ConsumeWarpTankFuel"/> would take for a hop of
        /// <paramref name="distance_m"/>. Returns 0 when free / no tank / trivial hop.
        /// </summary>
        internal static long EstimateWarpTankFuelUnits(Entity entity, double distance_m)
        {
            try
            {
                if (!entity.TryGetDataBlob<CargoStorageDB>(out var storage))
                    return 0;

                var cargoLib = entity.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                var (fuel, _) = entity.GetFuelInfo(cargoLib);
                if (fuel == null)
                    return 0;

                long stored = storage.GetUnitsStored(fuel, includeEscro: false);
                long free = storage.GetFreeUnitSpace(fuel, includeEscro: false);
                long capacity = stored + free;
                if (capacity <= 0)
                    return 0;

                const double MetersPerAu = 149597870700.0;
                double au = Math.Max(0, distance_m / MetersPerAu);
                const double MinBillableAu = 0.01;
                if (au < 1e-6)
                    return 0;
                double fraction = au < MinBillableAu
                    ? Math.Clamp(0.015 * (au / MinBillableAu), 0, 0.015)
                    : Math.Clamp(0.015 + 0.05 * au, 0.015, 0.18);
                return Math.Max(1, (long)Math.Ceiling(capacity * fraction));
            }
            catch
            {
                return long.MaxValue;
            }
        }

        /// <summary>True when tank fuel and warp capacitors can cover a hop of <paramref name="distance_m"/>.</summary>
        internal static bool CanAffordWarpHop(Entity entity, double distance_m)
        {
            long fuelNeed = EstimateWarpTankFuelUnits(entity, distance_m);
            if (fuelNeed > 0)
            {
                try
                {
                    if (!entity.TryGetDataBlob<CargoStorageDB>(out var storage))
                        return false;
                    var cargoLib = entity.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                    var (fuel, _) = entity.GetFuelInfo(cargoLib);
                    if (fuel == null)
                        return false;
                    if (storage.GetUnitsStored(fuel, includeEscro: false) < fuelNeed)
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            if (!entity.TryGetDataBlob<WarpAbilityDB>(out var warpDB)
                || !entity.TryGetDataBlob<EnergyGenAbilityDB>(out var powerDB))
                return true;

            if (!TryGetWarpEnergyNeed(warpDB, powerDB, distance_m,
                    out double needKJ, out _, out _, out _))
                return false;

            string eType = warpDB.EnergyType;
            if (string.IsNullOrEmpty(eType))
                return false;

            powerDB.EnergyStored.TryGetValue(eType, out double storedKJ);
            if (storedKJ + 1e-6 >= needKJ)
                return true;

            // Onboard generation can wait-fill before the hop (standing should not rush to colony).
            if (powerDB.TotalOutputMax > 1e-9
                && powerDB.EnergyStoreMax.TryGetValue(eType, out double max)
                && max + 1e-6 >= needKJ)
                return true;

            return false;
        }

        /// <summary>
        /// Drain cargo-tank fuel for a completed warp hop.
        /// Energy capacitors still pay bubble create/sustain/collapse; this is the visible
        /// tank cost so ships cannot roam the system forever on a full tank.
        /// Cost scales with distance and is hard-capped so one hop cannot empty the tank
        /// (the old StrictNewtonion circularisation burned ~90% per arrival).
        /// </summary>
        internal static void ConsumeWarpTankFuel(Entity entity, WarpMovingDB moveDB, DateTime atDateTime)
        {
            try
            {
                if (moveDB.WarpTankFuelConsumed)
                    return;

                if (!entity.TryGetDataBlob<CargoStorageDB>(out var storage))
                    return;

                var cargoLib = entity.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                var (fuel, _) = entity.GetFuelInfo(cargoLib);
                if (fuel == null)
                    return;

                long stored = storage.GetUnitsStored(fuel, includeEscro: false);
                long free = storage.GetFreeUnitSpace(fuel, includeEscro: false);
                long capacity = stored + free;
                if (capacity <= 0 || stored <= 0)
                    return;

                double distance_m = (moveDB.ExitPointAbsolute - moveDB.EntryPointAbsolute).Length();
                long want = EstimateWarpTankFuelUnits(entity, distance_m);
                if (want <= 0)
                    return;
                long take = Math.Min(want, stored);
                if (take <= 0)
                    return;

                double mass = take * fuel.MassPerUnit;
                CargoTransferProcessor.AddRemoveCargoMass(entity, fuel, -mass);
                moveDB.WarpTankFuelConsumed = true;

                const double MetersPerAu = 149597870700.0;
                double au = Math.Max(0, distance_m / MetersPerAu);
                DebugTraceLog.Info("Fuel",
                    $"ship#{entity.Id}: warp hop burned {take} units / {mass:0.#} kg " +
                    $"({100.0 * take / capacity:0.#}% of tank, {au:0.####} AU)",
                    atDateTime);
            }
            catch (Exception ex)
            {
                DebugTraceLog.Warn("Fuel",
                    $"ship#{entity.Id}: warp fuel consume failed: {ex.Message}",
                    atDateTime);
            }
        }

        /// <summary>
        /// Sets a circular orbit without newtonion movement or fuel use.
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="positionDB"></param>
        /// <param name="moveDB"></param>
        /// <param name="atDateTime"></param>
        /// <exception cref="NullReferenceException"></exception>
        static void SetOrbitHereNoNewt(Entity entity, WarpMovingDB moveDB, DateTime atDateTime)
        {
            if (moveDB.TargetEntity == null) throw new NullReferenceException("moveDB.TargetEntity cannot be null");

            PositionDB moveStatedb = entity.GetDataBlob<PositionDB>();
            Entity intendedTarget = moveDB.TargetEntity;

            // During warp PositionDB stays parented to the system root and is not stepped with
            // WarpMovingDB._position. Using that stale PositionDB for the SOI check treated every
            // arrival as "outside SOI" and parented the ship to the star — IsShipAtBody never
            // succeeded, so GeoSurvey kept re-warping in 1.5%-min micro-hops until Refuel preempted.
            moveStatedb.AbsolutePosition = moveDB.ExitPointAbsolute;

            double hopLen = (moveDB.ExitPointAbsolute - moveDB.EntryPointAbsolute).Length();
            // Fuel consume may skip ultra-tiny hops (no tank bill) — still watch for loops.
            MovementStuckWatchdog.NoteWarpHopCompleted(entity, intendedTarget, hopLen, atDateTime);

            double targetSOI = intendedTarget.GetSOI_m();
            // Body PositionDB is on the manager clock; arrival may be at PredictedExitTime.
            Vector3 targetAbsAtArrival = (Vector3)MoveMath.GetAbsoluteFuturePosition(intendedTarget, atDateTime);
            double distToTarget = (moveDB.ExitPointAbsolute - targetAbsAtArrival).Length();

            Entity? targetEntity;
            // Parent to the intended body when the exit actually landed in/near its SOI.
            // (Do not trust ExitPointrelative alone — a bad absolute exit must not force-parent
            // the hull to a moon/planet while it sits AU away near the star.)
            //
            // Micro-hop failsafe: if this hop was tiny (< 0.01 AU) the ship barely moved —
            // forcing the intended parent stops GeoSurvey re-warp loops when SOI math is flaky.
            const double MicroHopForceParent_m = 1_495_978_707.0; // 0.01 AU
            bool microHop = hopLen <= MicroHopForceParent_m;

            if (distToTarget <= targetSOI
                || (!double.IsInfinity(targetSOI)
                    && targetSOI > 0
                    && distToTarget <= targetSOI * 5)
                || (microHop && distToTarget <= Math.Max(targetSOI * 50, MicroHopForceParent_m)))
            {
                targetEntity = intendedTarget;
            }
            else if (intendedTarget.TryGetDataBlob<OrbitDB>(out var targetOrbit)
                     && targetOrbit.Parent != null)
            {
                // Truly outside SOI: fall back to parent (star / planet).
                targetEntity = targetOrbit.Parent;
            }
            else
            {
                targetEntity = intendedTarget;
            }

            if (targetEntity == null) throw new NullReferenceException("targetEntity cannot be null");

            //just chuck it in a circular orbit.
            OrbitDB newOrbit = OrbitDB.FromPosition(targetEntity, entity, atDateTime);
            entity.SetDataBlob(newOrbit);
            moveStatedb.SetParent(targetEntity);
            moveDB.IsAtTarget = true;

        }

        static void SetOrbitHereSimpleNewt(Entity entity, WarpMovingDB moveDB, DateTime atDateTime)
        {
            var newOrbit = moveDB.EndpointTargetOrbit;
            if (moveDB.TargetEntity is not { IsValid: true } targetEntity)
                throw new InvalidOperationException("Warp ended without valid target entity.");
            var mass = targetEntity.GetDataBlob<MassVolumeDB>().MassTotal;
            mass += entity.GetDataBlob<MassVolumeDB>().MassTotal;
            var sgp = GeneralMath.StandardGravitationalParameter(mass);

            var currentOrbit = OrbitMath.KeplerFromPositionAndVelocity(sgp, moveDB.ExitPointrelative, moveDB.SavedNewtonionVector, atDateTime);

            var target = targetEntity;
            NewtonSimpleMoveDB newtMove = new NewtonSimpleMoveDB(target, currentOrbit, newOrbit, atDateTime);
            entity.SetDataBlob(newtMove);
            NewtonSimpleProcessor.ProcessEntity(entity, atDateTime);

        }

        /// <summary>
        /// Sets an orbit using full newtonion movement and fuel use.
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="positionDB"></param>
        /// <param name="moveDB"></param>
        /// <param name="atDateTime"></param>
        /// <exception cref="NullReferenceException"></exception>
        static void SetOrbitHereFullNewt(Entity entity, WarpMovingDB moveDB, DateTime atDateTime)
        {
            if (moveDB.TargetEntity == null) throw new NullReferenceException("moveDB.TargetEntity cannot be null");
            //propulsionDB.CurrentVectorMS = new Vector3(0, 0, 0);
            var moveStatedb = entity.GetDataBlob<PositionDB>();
            double targetSOI = moveDB.TargetEntity.GetSOI_m();

            Entity? targetEntity;

            if (moveDB.TargetEntity.GetDataBlob<PositionDB>().GetDistanceTo_m(moveStatedb) > targetSOI)
            {
                targetEntity = moveDB.TargetEntity.GetDataBlob<OrbitDB>().Parent; //TODO: it's concevable we could be in another SOI not the parent (ie we could be in a target's moon's SOI)
            }
            else
            {
                targetEntity = moveDB.TargetEntity;
            }

            if (targetEntity == null) throw new NullReferenceException("targetEntity cannot be null");
            OrbitDB targetPlanetsOrbit = targetEntity.GetDataBlob<OrbitDB>();
            Vector3 insertionVector_m = OrbitProcessor.GetOrbitalInsertionVector(moveDB.SavedNewtonionVector, targetPlanetsOrbit, atDateTime);
            moveStatedb.SetParent(targetEntity);
            moveDB.IsAtTarget = true;

            OrbitDB newOrbit = OrbitDB.FromVelocity(targetEntity, entity, insertionVector_m, atDateTime);
            entity.SetDataBlob(newOrbit);

            var burnRate = entity.GetDataBlob<NewtonThrustAbilityDB>().FuelBurnRate;
            var exhaustVelocity = entity.GetDataBlob<NewtonThrustAbilityDB>().ExhaustVelocity;
            var mass = entity.GetDataBlob<MassVolumeDB>().MassTotal;

            /*
            if (moveDB.EndpointTargetExpendDeltaV.Length() != 0)
            {
                double fuelBurned = OrbitMath.TsiolkovskyFuelUse(mass, exhaustVelocity, moveDB.EndpointTargetExpendDeltaV.Length());
                double secondsBurn = fuelBurned / burnRate;
                var manuverNodeTime = entity.StarSysDateTime + TimeSpan.FromSeconds(secondsBurn * 0.5);

                NewtonThrustCommand.CreateCommand(entity.FactionOwnerID, entity, manuverNodeTime, moveDB.EndpointTargetExpendDeltaV, secondsBurn);
            }
            else if (moveDB.AutoCirculariseAfterWarp)
            {
                var sgp = GeneralMath.StandardGravitationalParameter(mass + targetEntity.GetDataBlob<MassVolumeDB>().MassTotal);
                var pos = positionDB.RelativePosition;
                double curSpeed = insertionVector_m.Length();
                double circSpeed = OrbitalMath.InstantaneousOrbitalSpeed(sgp, pos.Length(), pos.Length());
                double speediff = circSpeed - curSpeed;
                Vector3 circularizationBurn = speediff * Vector3.Normalise(insertionVector_m);

                double fuelBurned = OrbitMath.TsiolkovskyFuelUse(mass, exhaustVelocity, circularizationBurn.Length());
                double secondsBurn = fuelBurned / burnRate;
                var manuverNodeTime = entity.StarSysDateTime + TimeSpan.FromSeconds(secondsBurn * 0.5);

                NewtonThrustCommand.CreateCommand(entity.FactionOwnerID, entity, manuverNodeTime, circularizationBurn, secondsBurn);
            }
*/
        }


    }


}
