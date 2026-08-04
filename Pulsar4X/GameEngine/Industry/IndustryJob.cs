using System.Collections.Generic;
using Newtonsoft.Json;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Factions;
using Pulsar4X.Interfaces;

namespace Pulsar4X.Industry
{
    public class IndustryJob : JobBase
    {
        internal string? TypeID;
        public IndustryJobStatus Status { get; internal set; } = IndustryJobStatus.Queued;

        /// <summary>
        /// Deserialization only. Without this, Newtonsoft picks
        /// <see cref="IndustryJob(FactionInfoDB, string)"/> and passes a null factionInfo
        /// (not present in the JSON), which NullRefs and aborts the whole save load.
        /// </summary>
        [JsonConstructor]
        private IndustryJob()
        {
        }

        public IndustryJob(FactionInfoDB factionInfo, string itemID)
        {
            ItemGuid = itemID;
            var design = factionInfo.IndustryDesigns[itemID];
            TypeID = design.IndustryTypeID;
            Name = design.Name;
            if (design.ResourceCosts != null)
            {
                ResourcesRequiredRemaining = new Dictionary<string, long>(design.ResourceCosts);
            }
            else
            {
                ResourcesRequiredRemaining = new();
            }
            ResourcesCosts = design.ResourceCosts ?? new Dictionary<string, long>();
            ProductionPointsLeft = design.IndustryPointCosts;
            ProductionPointsCost = design.IndustryPointCosts;
            NumberOrdered = 1;
        }

        internal IndustryJob(IConstructableDesign design)
        {
            ItemGuid = design.UniqueID;
            TypeID = design.IndustryTypeID;
            Name = design.Name;
            ResourcesRequiredRemaining = new Dictionary<string, long>(design.ResourceCosts);
            ResourcesCosts = design.ResourceCosts ?? new Dictionary<string, long>();
            ProductionPointsLeft = design.IndustryPointCosts;
            ProductionPointsCost = design.IndustryPointCosts;
            NumberOrdered = 1;
        }

        public Entity? InstallOn { get; set; } = null;

        public override void InitialiseJob(ushort numberOrderd, bool auto)
        {
            NumberOrdered = numberOrderd;
            NumberCompleted = 0;
            Auto = auto;
        }
    }
}
