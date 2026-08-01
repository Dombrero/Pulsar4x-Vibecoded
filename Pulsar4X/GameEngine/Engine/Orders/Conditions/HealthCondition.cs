using System;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;
using Pulsar4X.Ships;

namespace Pulsar4X.Engine.Orders
{
    /// <summary>
    /// Fleet-average component health as a percentage (0–100).
    /// </summary>
    public class HealthCondition : ComparisonCondition
    {
        public HealthCondition(float threshold, ComparisonType comparisonType) : base(threshold, comparisonType)
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

            double totalHealthPercent = 0;
            int counted = 0;
            foreach (var ship in ships)
            {
                if (!ship.TryGetDataBlob<ComponentInstancesDB>(out var instances) || instances.AllComponents.Count == 0)
                    continue;

                double shipAvg = instances.AllComponents.Values.Average(c => c.HealthPercent) * 100.0;
                totalHealthPercent += shipAvg;
                counted++;
            }

            if (counted == 0)
                return false;

            var average = Math.Round(totalHealthPercent / counted);
            return Compare(average);
        }
    }
}
