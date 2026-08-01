using System;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Ships;

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
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            var ships = fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()).ToList();
            if (ships.Count == 0)
                return false;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;

            double totalFuelPercentage = 0;
            foreach (var ship in ships)
                totalFuelPercentage += ship.GetFuelPercent(cargoLibrary);

            // Round the average so EqualTo has a chance to fire.
            var average = Math.Round(totalFuelPercentage / ships.Count);
            return Compare(average);
        }
    }
}
