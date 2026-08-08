using System;
using System.Linq;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;

namespace Pulsar4X.Engine.Orders
{
    public class FuelCondition : ComparisonCondition
    {
        public FuelCondition(float threshold, ComparisonType comparisonType) : base(threshold, comparisonType)
        {
            Description = "percent";
            MaxValue = 100;
            MinValue = 0;
        }

        public override bool Evaluate(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                return false;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            var percents = FleetFuel.FuelCapableShips(fleet, cargoLibrary)
                .Select(s => s.GetFuelPercent(cargoLibrary))
                .ToList();
            if (percents.Count == 0)
                return false;

            // LessThan*: any hull below threshold (SensorSats without tanks must not dilute ENTER).
            // Other ops: average of fuel-capable ships only.
            if (ComparisionType is ComparisonType.LessThan or ComparisonType.LessThanOrEqual)
                return percents.Any(p => Compare(Math.Round(p)));

            var average = Math.Round(percents.Average());
            return Compare(average);
        }
    }
}
