using System;
using System.Collections.Generic;
using Pulsar4X.Colonies;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;

namespace Pulsar4X.Industry
{
    public static class MiningHelper
    {
        /// <summary>
        /// Effective units/day (double) after accessibility and mining bonuses.
        /// </summary>
        public static Dictionary<int, double> CalculateExactMiningRates(Entity colonyEntity)
        {
            if (!colonyEntity.TryGetDataBlob<MiningDB>(out var miningDB))
                throw new Exception("Entity does not have MiningDB");
            if (!colonyEntity.TryGetDataBlob<ColonyInfoDB>(out var colonyInfoDB))
                throw new Exception("Entity does not have ColonyInfoDB");
            if (!colonyInfoDB.PlanetEntity.TryGetDataBlob<MineralsDB>(out var mineralsDB))
                throw new Exception("Planet entity does not have MineralsDB");

            float miningBonuses = 1.0f;
            if (colonyEntity.TryGetDataBlob<ColonyBonusesDB>(out var colonyBonusesDB))
            {
                miningBonuses = colonyBonusesDB.GetBonus(AbilityType.Mine);
            }

            var planetMinerals = mineralsDB.Minerals;
            var mineRates = new Dictionary<int, double>(miningDB.BaseMiningRate.Count);
            foreach (var (key, baseRate) in miningDB.BaseMiningRate)
            {
                double accessibility = planetMinerals.ContainsKey(key) ? planetMinerals[key].Accessibility : 0;
                mineRates[key] = baseRate * miningBonuses * accessibility;
            }

            return mineRates;
        }

        /// <summary>
        /// Integer rates for <see cref="MiningDB.ActualMiningRate"/> (save-compatible long dict).
        /// Floors; sub-1 production is handled via <see cref="MiningDB.MiningRemainder"/>.
        /// </summary>
        public static Dictionary<int, long> CalculateActualMiningRates(Entity colonyEntity)
        {
            var exact = CalculateExactMiningRates(colonyEntity);
            var rounded = new Dictionary<int, long>(exact.Count);
            foreach (var (key, rate) in exact)
                rounded[key] = (long)Math.Floor(rate);
            return rounded;
        }
    }
}
