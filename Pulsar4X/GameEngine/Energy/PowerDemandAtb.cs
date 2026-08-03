using Newtonsoft.Json;

using Pulsar4X.Colonies;

using Pulsar4X.Components;

using Pulsar4X.Engine;

using Pulsar4X.Interfaces;



namespace Pulsar4X.Energy

{

    /// <summary>Continuous power draw (kW) for colony installations such as AutoMines.</summary>

    public class PowerDemandAtb : IComponentDesignAttribute

    {

        [JsonProperty]

        public double DemandKW { get; private set; }



        [JsonConstructor]

        private PowerDemandAtb() { }



        public PowerDemandAtb(double demandKW)

        {

            DemandKW = demandKW;

        }



        public void OnComponentInstallation(Entity parentEntity, ComponentInstance componentInstance)

        {

            if (!parentEntity.HasDataBlob<ColonyInfoDB>())

                return;



            PowerGenerationAtb.EnsureColonyPower(parentEntity);

            ColonyPowerProcessor.RecalcAbilities(parentEntity);

        }



        public void OnComponentUninstallation(Entity parentEntity, ComponentInstance componentInstance)

        {

            if (!parentEntity.HasDataBlob<ColonyInfoDB>())

                return;



            ColonyPowerProcessor.RecalcAbilities(parentEntity);

        }



        public string AtbName() => "Power Demand";



        public string AtbDescription() => $"Nameplate draw {DemandKW:0.##} kW";

    }

}


