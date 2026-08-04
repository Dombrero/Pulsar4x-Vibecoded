using System.Collections.Generic;

namespace Pulsar4X.Blueprints
{
    public class ThemeBlueprint : Blueprint
    {
        public string? Name { get; set; }
        public List<string> SystemNames { get; set; } = new();
        public List<string> FleetNames { get; set; } = new();
        public List<string> ShipNames { get; set; } = new();
        public List<string> FirstNames { get; set; } = new();
        public List<string> LastNames { get; set; } = new();
        public Dictionary<int, string> NavyRanks { get; set; } = new();
        public Dictionary<int, string> NavyRanksAbbreviations { get; set; } = new();
    }
}
