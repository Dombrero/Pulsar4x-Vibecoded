using System;
using System.Collections.Generic;
using Pulsar4X.Api;

namespace Pulsar4X.Client
{
    /// <summary>
    /// Fleet Issue Orders live on the fleet entity; member ships may also have their own queue
    /// or an engine-projected <see cref="ActivityView"/> for work that lives as ship state
    /// (recharge blob, geo/grav survey blob, warp).
    /// </summary>
    internal static class OrderDisplayHelpers
    {
        /// <summary>
        /// Headline for map labels: this ship's activity first, else the parent fleet mission.
        /// </summary>
        public static OrderSnapshot? ResolveCurrentOrder(
            IGameClient? client, int entityId, OrdersView? ownOrders, ActivityView? activity = null)
        {
            var shipOrders = GetShipActivityOrders(ownOrders, activity);
            if (shipOrders.Count > 0)
                return shipOrders[0];

            var fleet = GetFleetOrders(client, entityId);
            if (fleet.Count > 0)
                return fleet[0];

            return null;
        }

        /// <summary>Orders queued on this entity (ship or fleet body).</summary>
        public static IReadOnlyList<OrderSnapshot> GetShipOrders(OrdersView? ownOrders)
        {
            if (ownOrders?.Orders is { Count: > 0 } orders)
                return orders;
            return Array.Empty<OrderSnapshot>();
        }

        /// <summary>
        /// What this ship is doing: its own order queue, otherwise the engine's
        /// <see cref="ActivityView"/> (recharging, surveying, warping, idle).
        /// </summary>
        public static IReadOnlyList<OrderSnapshot> GetShipActivityOrders(
            OrdersView? ownOrders, ActivityView? activity)
        {
            var own = GetShipOrders(ownOrders);
            if (own.Count > 0)
                return own;

            if (activity != null
                && !string.IsNullOrWhiteSpace(activity.Name)
                && !activity.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    new OrderSnapshot(activity.Name, IsRunning: true, IsFinished: false, activity.Details),
                };
            }

            return Array.Empty<OrderSnapshot>();
        }

        /// <summary>
        /// Parent fleet mission when <paramref name="entityId"/> is a member ship,
        /// or this entity's own fleet queue when it is the fleet root.
        /// </summary>
        public static IReadOnlyList<OrderSnapshot> GetFleetOrders(IGameClient? client, int entityId)
        {
            var fleets = client?.Galaxy.Fleets;
            if (fleets == null)
                return Array.Empty<OrderSnapshot>();

            var parent = FindFleetContainingShip(fleets, entityId);
            if (parent != null)
                return FleetOrdersOrStatus(parent);

            var asFleet = FindFleetById(fleets, entityId);
            if (asFleet != null)
                return FleetOrdersOrStatus(asFleet);

            return Array.Empty<OrderSnapshot>();
        }

        /// <summary>True when entityId is a ship listed under a fleet (not the fleet root itself).</summary>
        public static bool IsFleetMember(IGameClient? client, int entityId)
        {
            var fleets = client?.Galaxy.Fleets;
            return fleets != null && FindFleetContainingShip(fleets, entityId) != null;
        }

        private static IReadOnlyList<OrderSnapshot> FleetOrdersOrStatus(FleetSnapshot fleet)
        {
            if (fleet.Orders is { Count: > 0 })
                return fleet.Orders;
            if (!string.IsNullOrWhiteSpace(fleet.StatusMessage))
                return new[] { StatusAsOrder(fleet.StatusMessage!) };
            return Array.Empty<OrderSnapshot>();
        }

        private static OrderSnapshot StatusAsOrder(string status)
            => new(status, IsRunning: false, IsFinished: false, Details: "Standing");

        public static FleetSnapshot? FindFleetContainingShip(IReadOnlyList<FleetSnapshot> fleets, int shipId)
        {
            foreach (var fleet in fleets)
            {
                foreach (var ship in fleet.Ships)
                {
                    if (ship.Id == shipId)
                        return fleet;
                }

                if (FindFleetContainingShip(fleet.SubFleets, shipId) is { } nested)
                    return nested;
            }

            return null;
        }

        public static FleetSnapshot? FindFleetById(IReadOnlyList<FleetSnapshot> fleets, int fleetId)
        {
            foreach (var fleet in fleets)
            {
                if (fleet.Id == fleetId)
                    return fleet;
                if (FindFleetById(fleet.SubFleets, fleetId) is { } nested)
                    return nested;
            }

            return null;
        }
    }
}
