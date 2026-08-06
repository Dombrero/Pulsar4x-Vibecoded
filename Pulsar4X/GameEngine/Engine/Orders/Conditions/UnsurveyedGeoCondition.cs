using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;
using Pulsar4X.GeoSurveys;

namespace Pulsar4X.Engine.Orders
{
    /// <summary>
    /// Count of eligible geo-survey targets in the flagship's system
    /// (unsurveyed bodies without an owned colony).
    /// </summary>
    public class UnsurveyedGeoCondition : ComparisonCondition
    {
        public UnsurveyedGeoCondition(float threshold, ComparisonType comparisonType) : base(threshold, comparisonType)
        {
            Description = "targets";
            MaxValue = 50;
            MinValue = 0;
        }

        public override bool Evaluate(Entity fleet)
        {
            if (!TryGetFlagshipSystem(fleet, out var manager))
                return false;

            int count = GeoSurveyTargets.CountEligible(manager, fleet.FactionOwnerID);
            return Compare(count);
        }

        internal static bool TryGetFlagshipSystem(Entity fleet, out EntityManager? manager)
            => FleetFlagshipSync.TryGetFlagshipSystem(fleet, out manager);
    }
}
