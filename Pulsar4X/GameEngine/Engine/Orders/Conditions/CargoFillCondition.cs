using System;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Engine.Orders
{
    /// <summary>
    /// Fleet-average cargo volume fill as a percentage (0–100).
    /// Ships without cargo storage are skipped; empty fleet → false.
    /// </summary>
    public class CargoFillCondition : ComparisonCondition
    {
        public CargoFillCondition(float threshold, ComparisonType comparisonType) : base(threshold, comparisonType)
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

            double totalFillPercent = 0;
            int counted = 0;
            foreach (var ship in ships)
            {
                if (!ship.TryGetDataBlob<CargoStorageDB>(out var storage) || storage.TypeStores.Count == 0)
                    continue;

                double maxVolume = 0;
                double usedVolume = 0;
                foreach (var kvp in storage.TypeStores)
                {
                    var store = kvp.Value;
                    if (store.MaxVolume <= 0)
                        continue;
                    maxVolume += store.MaxVolume;
                    usedVolume += Math.Max(0, store.MaxVolume - store.FreeVolume);
                }

                if (maxVolume <= 0)
                    continue;

                totalFillPercent += (usedVolume / maxVolume) * 100.0;
                counted++;
            }

            if (counted == 0)
                return false;

            var average = Math.Round(totalFillPercent / counted);
            return Compare(average);
        }
    }
}
