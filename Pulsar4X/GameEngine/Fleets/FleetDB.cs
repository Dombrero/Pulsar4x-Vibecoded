using Newtonsoft.Json;
using System;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine.Orders;

namespace Pulsar4X.Fleets
{
    public class FleetDB : TreeHierarchyDB
    {
        [JsonProperty]
        public int FlagShipID { get; internal set; } = -1;

        [JsonProperty]
        public bool InheritOrders { get; internal set; } = true;

        [JsonProperty]
        public SafeList<ConditionalOrder> StandingOrders { get; internal set; } = new();

        /// <summary>
        /// Index into <see cref="StandingOrders"/> for the mission the fleet is currently committed to.
        /// -1 = idle. Prevents standing orders from flickering / re-enqueueing every processor tick.
        /// </summary>
        [JsonProperty]
        public int ActiveStandingOrderIndex { get; internal set; } = -1;

        /// <summary>
        /// After a standing action vanishes immediately (no targets / travel fail), suppress
        /// re-ENTRY until this game time so we do not log restart/enqueue every hotloop.
        /// </summary>
        [JsonProperty]
        public DateTime? StandingSuppressUntil { get; set; }

        /// <summary>
        /// Shown in the UI instead of Idle when standing has nothing left to do
        /// (e.g. "Can't find more anomalies"). Cleared when a real standing action starts.
        /// </summary>
        [JsonProperty]
        public string? StandingStatusMessage { get; set; }

        /// <summary>
        /// Last star system id the flagship occupied when standing orders were evaluated.
        /// Used to detect jumps and clear stale suppress / cross-system survey queues.
        /// </summary>
        [JsonProperty]
        public string? StandingLastFlagshipSystemId { get; set; }

        /// <summary>
        /// Star system where the fleet last refuelled (for cross-system return when local stores are missing).
        /// </summary>
        [JsonProperty]
        public string? LastRefuelSystemId { get; set; }

        [JsonProperty]
        public int LastRefuelColonyId { get; set; } = -1;

        public FleetDB() : base(null) { }

        public override object Clone()
        {
            return new FleetDB();
        }
    }
}