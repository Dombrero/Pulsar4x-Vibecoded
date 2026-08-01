using System.Collections.Generic;
using Newtonsoft.Json;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;

namespace Pulsar4X.Industry
{
    public class MiningDB : BaseDataBlob, IAbilityDescription
    {
        [JsonProperty]
        public Dictionary<int, long> BaseMiningRate { get; set; }

        /// <summary>
        /// Integer snapshot for UI/legacy; actual mining uses <see cref="BaseMiningRate"/> with
        /// fractional accumulation in <see cref="MiningRemainder"/> so old saves keep loading
        /// (<c>Dictionary&lt;int,long&gt;</c> $type in JSON).
        /// </summary>
        [JsonProperty]
        public Dictionary<int, long> ActualMiningRate { get; set; }

        /// <summary>Fractional units carried between daily ticks (absent in old saves → null OK).</summary>
        [JsonProperty]
        public Dictionary<int, double>? MiningRemainder { get; set; }

        [JsonProperty]
        public int NumberOfMines { get; set;} = 0;

        public Dictionary<int, MineralDeposit> MineralDeposit => OwningEntity.GetDataBlob<ColonyInfoDB>().PlanetEntity.GetDataBlob<MineralsDB>().Minerals;

        public MiningDB()
        {
            BaseMiningRate = new Dictionary<int, long>();
            ActualMiningRate = new Dictionary<int, long>();
            MiningRemainder = new Dictionary<int, double>();
        }

        public MiningDB(MiningDB db)
        {
            BaseMiningRate = new Dictionary<int, long>(db.BaseMiningRate);
            ActualMiningRate = new Dictionary<int, long>(db.ActualMiningRate);
            MiningRemainder = db.MiningRemainder != null
                ? new Dictionary<int, double>(db.MiningRemainder)
                : new Dictionary<int, double>();
            NumberOfMines = db.NumberOfMines;
        }

        public override object Clone()
        {
            return new MiningDB(this);
        }

        public string AbilityName()
        {
            return "Resource Mining";
        }

        public string AbilityDescription()
        {
            return "Mines Resources at Rates of: \n";
        }
    }
}
