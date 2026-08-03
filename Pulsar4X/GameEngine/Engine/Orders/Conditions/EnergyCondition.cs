using System;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;
using Pulsar4X.Ships;

namespace Pulsar4X.Engine.Orders
{
    public class EnergyCondition : ComparisonCondition
    {
        public EnergyCondition(float threshold, ComparisonType comparisonType) : base(threshold, comparisonType)
        {
            Description = "percent";
            MaxValue = 100;
            MinValue = 0;
        }

        public override bool Evaluate(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            var ships = fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()).ToList();
            if (ships.Count == 0)
                return false;

            double total = 0;
            foreach (var ship in ships)
                total += EnergyRechargeHelper.GetShipEnergyPercent(ship);

            var average = Math.Round(total / ships.Count);
            return Compare(average);
        }
    }
}
