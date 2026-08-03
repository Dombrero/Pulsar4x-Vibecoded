using System;
using Newtonsoft.Json;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;

namespace Pulsar4X.Energy
{
    /// <summary>
    /// Active dock recharge from a colony into a ship's <see cref="EnergyGenAbilityDB"/> batteries.
    /// Removed when the ship is full or the colony can no longer supply.
    /// </summary>
    public class EnergyRechargeDB : BaseDataBlob
    {
        [JsonProperty]
        public int ColonyEntityId { get; set; }

        [JsonProperty]
        public double RateKW { get; set; }

        [JsonIgnore]
        public Entity? ColonyEntity { get; set; }

        public EnergyRechargeDB() { }

        public EnergyRechargeDB(Entity colony, double rateKW)
        {
            ColonyEntity = colony;
            ColonyEntityId = colony.Id;
            RateKW = rateKW;
        }

        public EnergyRechargeDB(EnergyRechargeDB other)
        {
            ColonyEntityId = other.ColonyEntityId;
            RateKW = other.RateKW;
            ColonyEntity = other.ColonyEntity;
        }

        public override object Clone() => new EnergyRechargeDB(this);
    }
}
