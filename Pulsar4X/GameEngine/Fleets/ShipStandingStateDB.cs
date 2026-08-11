using System;
using Newtonsoft.Json;
using Pulsar4X.Datablobs;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Per-ship standing commitment. Fleet <see cref="FleetDB.StandingOrders"/> is only a template;
    /// each hull picks and runs its own index independently.
    /// </summary>
    public class ShipStandingStateDB : BaseDataBlob
    {
        /// <summary>Index into the owning fleet's StandingOrders template. -1 = idle.</summary>
        [JsonProperty]
        public int ActiveStandingOrderIndex { get; set; } = -1;

        /// <summary>Back off re-ENTRY until this game time after an empty / failed run.</summary>
        [JsonProperty]
        public DateTime? SuppressUntil { get; set; }

        /// <summary>Optional UI hint for this hull (e.g. no local targets).</summary>
        [JsonProperty]
        public string? StatusMessage { get; set; }

        /// <summary>Last system id observed for this ship (local standing sync after jump).</summary>
        [JsonProperty]
        public string? LastSystemId { get; set; }

        public override object Clone()
        {
            return new ShipStandingStateDB
            {
                ActiveStandingOrderIndex = ActiveStandingOrderIndex,
                SuppressUntil = SuppressUntil,
                StatusMessage = StatusMessage,
                LastSystemId = LastSystemId,
            };
        }
    }
}
