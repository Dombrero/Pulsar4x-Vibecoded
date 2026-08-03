using System.Collections.Generic;
using Pulsar4X.Engine;
using Pulsar4X.Interfaces;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Stringify = Pulsar4X.Api.Stringify;

namespace Pulsar4X.Industry
{
    public class MineResourcesAtbDB : BaseDataBlob, IComponentDesignAttribute
    {
        public Dictionary<string, double> ResourcesPerEconTick { get; internal set; }

        public MineResourcesAtbDB() { }

        /// <summary>
        /// Component factory constructor. Rates stay as doubles so fractional
        /// template values can accumulate across ticks via mining remainder.
        /// </summary>
        /// <param name="resources">units per economy tick</param>
        public MineResourcesAtbDB(Dictionary<string, double> resources)
        {
            ResourcesPerEconTick = new Dictionary<string, double>(resources);
        }

        public MineResourcesAtbDB(MineResourcesAtbDB db)
        {
            ResourcesPerEconTick = db.ResourcesPerEconTick;
        }

        public override object Clone()
        {
            return new MineResourcesAtbDB(this);
        }

        public void OnComponentInstallation(Entity parentEntity, ComponentInstance componentInstance)
        {
            if (!parentEntity.TryGetDataBlob<MiningDB>(out var miningDB))
            {
                parentEntity.SetDataBlob(new MiningDB() { NumberOfMines = 1 });
            }
            else
            {
                miningDB.NumberOfMines++;
            }
            MineResourcesProcessor.CalcMaxRate(parentEntity);
        }

        public void OnComponentUninstallation(Entity parentEntity, ComponentInstance componentInstance)
        {
            if(parentEntity.TryGetDataBlob<MiningDB>(out var miningDB))
            {
                miningDB.NumberOfMines--;

                if(miningDB.NumberOfMines == 0)
                {
                    parentEntity.RemoveDataBlob<MiningDB>();
                }
                else
                {
                    MineResourcesProcessor.CalcMaxRate(parentEntity);
                }
            }
        }

        public string AtbName()
        {
            return "Resource Mining";
        }

        public string AtbDescription()
        {
            // FIXME:
            //string time = StaticRefLib.Game.Settings.EconomyCycleTime.ToString();
            string desc = "Adds to Resource Mining Ability at Rates of: \n";
            foreach (var kvp in ResourcesPerEconTick)
            {
                //string resourceName = StaticRefLib.StaticData.CargoGoods.GetMineral(kvp.Key).Name;
                //desc += resourceName + "\t" + Stringify.Number(kvp.Value) + "\n";
            }

            return desc;// + "per " + time;
        }
    }
}