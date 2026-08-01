using System.Collections.Generic;
using Pulsar4X.Api;

namespace Pulsar4X.Client
{
    /// <summary>
    /// Fleet Issue Orders live on the fleet entity; member ships often have an empty queue
    /// while still executing that mission. Resolve what the UI should show as "current order".
    /// </summary>
    internal static class OrderDisplayHelpers
    {
        public static OrderSnapshot? ResolveCurrentOrder(IGameClient? client, int entityId, OrdersView? ownOrders)
        {
            var fleets = client?.Galaxy.Fleets;
            if (fleets != null)
            {
                var fleet = FindFleetContainingShip(fleets, entityId);
                if (fleet?.Orders is { Count: > 0 })
                    return fleet.Orders[0];
                if (!string.IsNullOrWhiteSpace(fleet?.StatusMessage))
                    return StatusAsOrder(fleet!.StatusMessage!);
            }

            if (ownOrders?.Orders is { Count: > 0 } shipOrders)
                return shipOrders[0];

            // Fleet entity itself (not a member ship).
            if (fleets != null)
            {
                var asFleet = FindFleetById(fleets, entityId);
                if (asFleet?.Orders is { Count: > 0 })
                    return asFleet.Orders[0];
                if (!string.IsNullOrWhiteSpace(asFleet?.StatusMessage))
                    return StatusAsOrder(asFleet!.StatusMessage!);
            }

            return null;
        }

        /// <summary>
        /// Ship's own queue, or the parent fleet's queue when the ship is idle under a fleet mission.
        /// </summary>
        public static IReadOnlyList<OrderSnapshot> ResolveOrdersList(IGameClient? client, int entityId, OrdersView? ownOrders)
        {
            if (ownOrders?.Orders is { Count: > 0 } shipOrders)
                return shipOrders;

            var fleets = client?.Galaxy.Fleets;
            if (fleets != null)
            {
                var fleet = FindFleetContainingShip(fleets, entityId) ?? FindFleetById(fleets, entityId);
                if (fleet?.Orders is { Count: > 0 })
                    return fleet.Orders;
                if (!string.IsNullOrWhiteSpace(fleet?.StatusMessage))
                    return new[] { StatusAsOrder(fleet!.StatusMessage!) };
            }

            return System.Array.Empty<OrderSnapshot>();
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
