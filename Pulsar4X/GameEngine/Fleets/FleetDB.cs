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
        /// UI aggregate only — per-ship commitment lives on <see cref="ShipStandingStateDB"/>.
        /// Updated by <see cref="FleetOrderProcessor"/> from child hulls.
        /// </summary>
        [JsonProperty]
        public int ActiveStandingOrderIndex { get; internal set; } = -1;

        /// <summary>
        /// Reserved for a future "stay in formation" mode (flotilla-wide movement coordinators).
        /// Default false: each ship runs the standing template independently.
        /// </summary>
        [JsonProperty]
        public bool StayInFormation { get; internal set; } = false;

        /// <summary>
        /// After a standing action vanishes immediately (no targets / travel fail), suppress
        /// re-ENTRY until this game time so we do not log restart/enqueue every hotloop.
        /// Fleet-level aggregate; ships use <see cref="ShipStandingStateDB.SuppressUntil"/>.
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

        /// <summary>
        /// Jump gate entity id in the <em>current</em> system that the fleet arrived through.
        /// Prefer this gate when returning toward <see cref="LastRefuelSystemId"/> (reverse last hop).
        /// </summary>
        [JsonProperty]
        public int LastArrivalJumpGateId { get; set; } = -1;

        public FleetDB() : base(null) { }

        public override object Clone()
        {
            return new FleetDB();
        }
    }
}