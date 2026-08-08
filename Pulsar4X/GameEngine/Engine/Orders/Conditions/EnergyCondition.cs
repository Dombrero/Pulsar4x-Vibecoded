using System;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;

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
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                return false;

            // Standing colony-recharge ENTER: only battery-only hulls (no onboard generation).
            // Generator ships wait in place for the next task instead of docking.
            if (ComparisionType is ComparisonType.LessThan or ComparisonType.LessThanOrEqual)
            {
                foreach (var ship in FleetEnergy.EnergyCapableShips(fleet))
                {
                    if (FleetEnergy.HasOnboardGeneration(ship))
                        continue;
                    if (Compare(Math.Round(EnergyRechargeHelper.GetShipEnergyPercent(ship))))
                        return true;
                }
                return false;
            }

            var percents = FleetEnergy.EnergyCapableShips(fleet)
                .Select(s => EnergyRechargeHelper.GetShipEnergyPercent(s))
                .ToList();
            if (percents.Count == 0)
                return false;

            var average = Math.Round(percents.Average());
            return Compare(average);
        }
    }
}
