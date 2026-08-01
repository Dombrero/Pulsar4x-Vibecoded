using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;

namespace Pulsar4X.Engine.Orders
{
    /// <summary>
    /// Count of unsurveyed gravitational anomalies in the flagship's system.
    /// </summary>
    public class UnsurveyedAnomalyCondition : ComparisonCondition
    {
        public UnsurveyedAnomalyCondition(float threshold, ComparisonType comparisonType) : base(threshold, comparisonType)
        {
            Description = "targets";
            MaxValue = 50;
            MinValue = 0;
        }

        public override bool Evaluate(Entity fleet)
        {
            if (!UnsurveyedGeoCondition.TryGetFlagshipSystem(fleet, out var manager))
                return false;

            int factionId = fleet.FactionOwnerID;
            int count = CountUnsurveyed(manager, factionId);
            return Compare(count);
        }

        /// <summary>Same eligibility rules as <see cref="MoveToNearestGravSurveyAction"/>.</summary>
        public static int CountUnsurveyed(EntityManager manager, int factionId)
        {
            int count = 0;
            foreach (var entity in manager.GetAllEntitiesWithDataBlob<JPSurveyableDB>())
            {
                if (!entity.TryGetDataBlob<JPSurveyableDB>(out var db) || db == null)
                    continue;
                if (db.IsSurveyComplete(factionId))
                    continue;
                // FindNearest also requires a position — count only reachable survey targets.
                if (!entity.TryGetDataBlob<PositionDB>(out _))
                    continue;
                count++;
            }
            return count;
        }
    }
}
