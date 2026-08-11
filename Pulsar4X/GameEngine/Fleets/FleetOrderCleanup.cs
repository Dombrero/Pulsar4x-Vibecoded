using System;
using System.Linq;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.GeoSurveys;
using Pulsar4X.JumpPoints;
using Pulsar4X.Messaging;
using Pulsar4X.Movement;
using Pulsar4X.Orbits;
using Pulsar4X.Storage;
using Pulsar4X.Galaxy;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Clears stuck fleet/ship order queues — especially cargo transfers that lock the Movement lane
    /// and leave "Waiting to start" forever when issued out of range.
    /// </summary>
    public static class FleetOrderCleanup
    {
        public static void AbortCargoTransfersOnEntity(Entity entity)
        {
            if (!entity.TryGetDataBlob<OrderableDB>(out var orderable))
                return;

            foreach (var transfer in orderable.ActionList.OfType<CargoTransferOrder>().ToList())
                transfer.Abort();
        }

        public static void AbortCargoTransfersOnFleetShips(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return;

            foreach (var ship in GetAssignedShips(fleetDB))
                AbortCargoTransfersOnEntity(ship);
        }

        /// <summary>
        /// Cancel ship-level warp orders / in-progress warp only.
        /// Does NOT abort cargo transfers — killing a refuel transfer every Move tick
        /// is what caused Fuel&lt;30% standing orders to loop forever at the colony.
        /// </summary>
        public static void AbortShipMovementOrders(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return;

            foreach (var ship in GetAssignedShips(fleetDB))
                AbortShipMovementOrdersOnEntity(ship);
        }

        /// <summary>
        /// Cancel warps only — does not remove survey/jump orders (safe from within survey EnsureTravel).
        /// </summary>
        public static void AbortShipWarpsOnlyOnEntity(Entity ship)
        {
            if (ship.TryGetDataBlob<OrderableDB>(out var orderable))
            {
                foreach (var warpCmd in orderable.ActionList.OfType<WarpMoveCommand>().ToList())
                {
                    warpCmd.CancelInPlace();
                    orderable.ActionList.Remove(warpCmd);
                }
            }

            if (ship.HasDataBlob<WarpMovingDB>())
                AbortWarpToLocalOrbit(ship);
        }

        public static void AbortShipMovementOrdersOnEntity(Entity ship)
        {
            if (ship.TryGetDataBlob<OrderableDB>(out var orderable))
            {
                foreach (var warpCmd in orderable.ActionList.OfType<WarpMoveCommand>().ToList())
                {
                    warpCmd.CancelInPlace();
                    orderable.ActionList.Remove(warpCmd);
                }

                // Standing survey leftovers survive Refuel preempt if we only cancel warps —
                // ships then "Geo Survey Luna" while parked at a jump point after returning home.
                foreach (var survey in orderable.ActionList
                             .Where(a => a is GeoSurveyOrder or JPSurveyOrder)
                             .ToList())
                {
                    survey.Status = ActionStatus.Failed;
                    orderable.ActionList.Remove(survey);
                }

                PublishOrdersChanged(ship);
            }

            if (ship.HasDataBlob<GeoSurveyingDB>())
                ship.RemoveDataBlob<GeoSurveyingDB>();
            if (ship.HasDataBlob<JPSurveyDB>())
                ship.RemoveDataBlob<JPSurveyDB>();

            if (ship.HasDataBlob<WarpMovingDB>())
                AbortWarpToLocalOrbit(ship);
        }

        /// <summary>
        /// Clear warps AND cargo so a new destination (e.g. geo survey) can take the Movement lane.
        /// Use only when leaving the current body — not on every standing-order re-eval.
        /// </summary>
        public static void AbortShipOrdersBlockingMovement(Entity fleet)
        {
            AbortCargoTransfersOnFleetShips(fleet);
            AbortShipMovementOrders(fleet);
        }

        /// <summary>
        /// Drop out of warp at the current absolute position into a circular orbit around the star.
        /// </summary>
        public static void AbortWarpToLocalOrbit(Entity ship)
        {
            if (!ship.TryGetDataBlob<WarpMovingDB>(out _))
                return;
            if (!ship.TryGetDataBlob<PositionDB>(out var positionDB))
            {
                ship.RemoveDataBlob<WarpMovingDB>();
                return;
            }

            // Capture before removing warp — AbsolutePosition depends on parent chain.
            var absolute = positionDB.AbsolutePosition;
            var parent = positionDB.Root;

            ship.RemoveDataBlob<WarpMovingDB>();

            if (parent == null || parent == ship)
            {
                // Hover-abort / free space: freeze absolute pose. Must clear MoveType.Warp or the
                // next WarpMoveCommand constructor calls GetAbsoluteFuturePosition → GetDataBlob
                // WarpMovingDB and throws KeyNotFoundException (stuck after grav anomaly #1).
                positionDB.AbsolutePosition = absolute;
                WarpMoveProcessor.AttachToSystemWarpFrame(ship, positionDB);
                positionDB.AbsolutePosition = absolute;
                positionDB.MoveType = PositionDB.MoveTypes.None;
                return;
            }

            // Parked on a static body (grav anomaly / JP): do not invent an orbit around
            // a near-zero-mass parent — freeze pose in the heliocentric frame so the next
            // hop does not double-apply AbsolutePosition under the anomaly parent.
            if (parent.TryGetDataBlob<PositionDB>(out var parentPos)
                && parentPos.MoveType == PositionDB.MoveTypes.None)
            {
                positionDB.AbsolutePosition = absolute;
                WarpMoveProcessor.AttachToSystemWarpFrame(ship, positionDB);
                positionDB.AbsolutePosition = absolute;
                positionDB.MoveType = PositionDB.MoveTypes.None;
                return;
            }

            try
            {
                var at = ship.StarSysDateTime;
                var parentAbs = parent.GetDataBlob<PositionDB>().AbsolutePosition;
                var relative = absolute - parentAbs;
                double mass = ship.TryGetDataBlob<MassVolumeDB>(out var mv) ? mv.MassTotal : 1;
                var newOrbit = OrbitDB.FromPosition(parent, relative, mass, at);
                ship.SetDataBlob(newOrbit);
                positionDB.SetParent(parent);
                positionDB.MoveType = PositionDB.MoveTypes.Orbit;
            }
            catch
            {
                if (ship.HasDataBlob<WarpMovingDB>())
                    ship.RemoveDataBlob<WarpMovingDB>();
                positionDB.AbsolutePosition = absolute;
                positionDB.MoveType = PositionDB.MoveTypes.None;
            }
        }

        public static void ClearAllOrders(Entity entity)
        {
            if (!entity.TryGetDataBlob<OrderableDB>(out var orderable))
                return;

            foreach (var transfer in orderable.ActionList.OfType<CargoTransferOrder>().ToList())
                transfer.Abort();

            foreach (var warpCmd in orderable.ActionList.OfType<WarpMoveCommand>().ToList())
                warpCmd.CancelInPlace();

            if (entity.HasDataBlob<WarpMovingDB>())
                AbortWarpToLocalOrbit(entity);

            orderable.ActionList.Clear();
            PublishOrdersChanged(entity);
        }

        public static void ClearFleetAndShipOrders(Entity fleet)
        {
            ClearAllOrders(fleet);

            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return;

            foreach (var ship in GetAssignedShips(fleetDB))
                ClearAllOrders(ship);
        }

        /// <summary>
        /// Player Issue Orders and top-level goals outrank Standing. Drop standing-sourced
        /// fleet work and clear commitment so Standing can re-enter cleanly once Issue/goal ends.
        /// </summary>
        public static void PauseStandingForPlayerIssue(Entity entity)
        {
            if (entity == null || !entity.IsValid)
                return;

            if (entity.TryGetDataBlob<OrderableDB>(out var orderableDB))
                orderableDB.ActionList.RemoveAll(a => a.Source == OrderSource.Standing);

            if (!entity.TryGetDataBlob<FleetDB>(out var fleetDB))
                return;

            // Without this, Issue while standing had drained its queue (but kept commitment)
            // left ActiveStandingOrderIndex stuck; after the Issue finished Standing could
            // sit Idle forever behind a stale Refuel/busy gate.
            fleetDB.ActiveStandingOrderIndex = -1;
            fleetDB.StandingSuppressUntil = null;
            AbortCargoTransfersOnFleetShips(entity);
            AbortShipMovementOrders(entity);
        }

        /// <summary>
        /// True when every warp-capable assigned ship is on-station at the colony.
        ///
        /// Per-ship, not "any child": a parked SensorSat at Earth must not skip the return
        /// warp for Surveyors still at Mercury. Non-warp hulls (static sats) are ignored for
        /// this travel gate — they neither force a false "at colony" nor block refuel when
        /// they cannot follow. Actual fuel transfers are still issued per hull via
        /// <see cref="IsShipAtColony"/>.
        /// </summary>
        public static bool IsFleetAtColony(Entity fleet, Entity colony)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB)
                || !colony.HasDataBlob<ColonyInfoDB>())
                return false;

            var travelers = GetAssignedShips(fleetDB)
                .Where(s => s.HasDataBlob<WarpAbilityDB>())
                .ToList();

            if (travelers.Count > 0)
                return travelers.All(ship => IsShipAtColony(ship, colony));

            // No warp ships — only treat as on-station if every cargo hull is there.
            var cargoShips = GetAssignedShips(fleetDB)
                .Where(s => s.HasDataBlob<CargoStorageDB>())
                .ToList();
            return cargoShips.Count > 0
                   && cargoShips.All(ship => IsShipAtColony(ship, colony));
        }

        /// <summary>
        /// On-station for refuel: parented to the colony entity, its planet, another colony
        /// on that planet, or inside the planet's SOI (bad warp-exit parented to the star).
        /// </summary>
        public static bool IsShipAtColony(Entity ship, Entity colony)
        {
            if (!colony.TryGetDataBlob<ColonyInfoDB>(out var colonyInfo))
                return false;
            if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || shipPos.Parent == null)
                return false;

            var parent = shipPos.Parent;
            // Warp-to-colony static exit parents the ship to the colony entity itself.
            if (parent.Id == colony.Id)
                return true;

            var planet = colonyInfo.PlanetEntity;
            if (planet != null && parent.Id == planet.Id)
                return true;

            // Another installation on the same world.
            if (planet != null
                && parent.TryGetDataBlob<ColonyInfoDB>(out var parentColony)
                && parentColony.PlanetEntity != null
                && parentColony.PlanetEntity.Id == planet.Id)
                return true;

            return planet != null && IsShipOrbiting(ship, planet);
        }

        /// <summary>
        /// True when at least one assigned ship orbits <paramref name="body"/>
        /// (or the colony's planet when <paramref name="body"/> is a colony).
        /// </summary>
        public static bool IsFleetAtBody(Entity fleet, Entity body)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            Entity orbitParent = body;
            if (body.TryGetDataBlob<ColonyInfoDB>(out var colonyInfo))
                orbitParent = colonyInfo.PlanetEntity;

            return GetAssignedShips(fleetDB).Any(ship => IsShipOrbiting(ship, orbitParent));
        }

        public static bool IsShipAtBody(Entity ship, Entity body)
        {
            Entity orbitParent = body;
            if (body.TryGetDataBlob<ColonyInfoDB>(out var colonyInfo))
                orbitParent = colonyInfo.PlanetEntity;

            return IsShipOrbiting(ship, orbitParent);
        }

        private static bool IsShipOrbiting(Entity ship, Entity orbitParent)
        {
            if (orbitParent == null || !ship.TryGetDataBlob<PositionDB>(out var shipPos))
                return false;
            var parent = shipPos.Parent;
            // Id compare: reference equality fails across some recreate/deserialize paths.
            if (parent != null && parent.Id == orbitParent.Id)
                return true;

            // On-station for survey/refuel: inside the body's SOI counts even if a bad warp exit
            // left the ship parented to the star near the body (legacy micro-hop loop state).
            if (!orbitParent.HasDataBlob<PositionDB>() || !orbitParent.HasDataBlob<MassVolumeDB>())
                return false;

            double dist = orbitParent.GetDataBlob<PositionDB>().GetDistanceTo_m(shipPos);
            double soi = orbitParent.GetSOI_m();
            // Stars/static bodies report ∞ SOI — never treat "anywhere in system" as on-station.
            if (double.IsInfinity(soi) || double.IsNaN(soi) || soi <= 0)
                return false;
            return dist <= soi;
        }

        private static System.Collections.Generic.IEnumerable<Entity> GetAssignedShips(FleetDB fleetDB)
            => fleetDB.Children.Where(c => !c.HasDataBlob<FleetDB>());

        private static void PublishOrdersChanged(Entity holder)
            => _ = MessagePublisher.Instance.Publish(Message.Create(
                MessageTypes.OrdersChanged,
                entityId: holder.Id,
                systemId: holder.AttachedManager.ManagerID,
                factionId: holder.FactionOwnerID));
    }
}
