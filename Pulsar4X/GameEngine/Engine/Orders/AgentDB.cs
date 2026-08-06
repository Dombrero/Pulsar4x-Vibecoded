using Pulsar4X.Datablobs;

namespace Pulsar4X.Engine.Orders;

/// <summary>
/// Personality traits that influence goal weighting for commanders/administrators.
/// </summary>
public class AgentDB : BaseDataBlob
{
    public float Aggression { get; set; } = 1f;
    public float Caution { get; set; } = 1f;
    public float Curiosity { get; set; } = 1f;
    public float Greed { get; set; } = 1f;
    public float Loyalty { get; set; } = 1f;

    public override object Clone()
    {
        return new AgentDB
        {
            Aggression = Aggression,
            Caution = Caution,
            Curiosity = Curiosity,
            Greed = Greed,
            Loyalty = Loyalty,
        };
    }
}
