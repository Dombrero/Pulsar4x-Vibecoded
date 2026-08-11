using System;
using Pulsar4X.DataStructures;
using Pulsar4X.Interfaces;
using Pulsar4X.Engine;

namespace Pulsar4X.Engine.Orders
{
    public abstract class ComparisonCondition : ICondition
    {
        public ComparisonType ComparisionType { get; set; }
        public float Threshold { get; set; }
        public float MaxValue { get; internal set; }
        public float MinValue { get; internal set; }
        public string Description { get; internal set; } = "";
        public ConditionDisplayType DisplayType { get; } = ConditionDisplayType.Comparison;

        public ComparisonCondition(float threshold, ComparisonType comparisonType)
        {
            Threshold = threshold;
            ComparisionType = comparisonType;
        }

        public abstract bool Evaluate(Entity fleet);

        /// <summary>
        /// Applies <see cref="ComparisionType"/> against <see cref="Threshold"/>.
        /// </summary>
        internal bool Compare(double value)
        {
            switch (ComparisionType)
            {
                case ComparisonType.LessThan:
                    return value < Threshold;
                case ComparisonType.LessThanOrEqual:
                    return value <= Threshold;
                case ComparisonType.EqualTo:
                    return value == Threshold;
                case ComparisonType.GreaterThan:
                    return value > Threshold;
                case ComparisonType.GreaterThanOrEqual:
                    return value >= Threshold;
                default:
                    throw new InvalidOperationException("Unknown comparison type.");
            }
        }
    }
}
