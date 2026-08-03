using System;
using Pulsar4X.Orbital;
using Pulsar4X.Interfaces;
using Pulsar4X.Engine;

namespace Pulsar4X.Energy
{
    public class EnergyGenProcessor : IInstanceProcessor
    {

        public static void EnergyGen(Entity entity, DateTime atDateTime)
        {
            EnergyGenAbilityDB energyGenDB = entity.GetDataBlob<EnergyGenAbilityDB>();

            TimeSpan t = atDateTime - energyGenDB.dateTimeLastProcess;
            double seconds = Math.Max(0, t.TotalSeconds);

            if (energyGenDB.EnergyType == null)
            {
                energyGenDB.dateTimeLastProcess = atDateTime;
                return;
            }

            string energyType = energyGenDB.EnergyType.UniqueID;
            if (!energyGenDB.EnergyStored.TryGetValue(energyType, out var stored))
                stored = 0;
            if (!energyGenDB.EnergyStoreMax.TryGetValue(energyType, out var storeMax))
                storeMax = 0;

            double freestore = Math.Max(0, storeMax - stored);
            double demandKW = energyGenDB.Demand;
            double genKW = energyGenDB.TotalOutputMax;

            // Net electrical power after serving active demand (warp, etc.).
            double surplusKW = Math.Max(0, genKW - demandKW);
            double deficitKW = Math.Max(0, demandKW - genKW);

            // Battery top-up from surplus is capped at the same accept rate used for dock recharge,
            // otherwise a multi-kW reactor fills a small buffer in seconds and colony recharge
            // looks instantaneous.
            double chargeCapKW = EnergyRechargeHelper.GetShipAcceptRateKW(entity);
            if (chargeCapKW <= 0)
                chargeCapKW = surplusKW;

            double chargeKW = Math.Min(surplusKW, chargeCapKW);
            double powerToStoreKW = deficitKW > 0 ? -deficitKW : chargeKW;

            double energyDelta = powerToStoreKW * Math.Max(seconds, 1e-6);
            // Legacy path: first process after spawn can have ~0 elapsed; still allow a tiny step
            // so interrupts keep scheduling.
            if (seconds <= 0)
                energyDelta = GeneralMath.Clamp(powerToStoreKW, -stored, freestore);
            else
                energyDelta = GeneralMath.Clamp(energyDelta, -stored, freestore);

            energyGenDB.EnergyStored[energyType] = stored + energyDelta;

            if (powerToStoreKW > 1e-9 && freestore > 1e-9)
            {
                double timeToFill = Math.Ceiling(freestore / powerToStoreKW);
                DateTime interuptTime = atDateTime + TimeSpan.FromSeconds(Math.Max(1, timeToFill));
                entity.Manager.ManagerSubpulses.AddEntityInterupt(interuptTime, nameof(EnergyGenProcessor), entity);
            }
            else if (powerToStoreKW < -1e-9 && stored > 1e-9)
            {
                double timeToEmpty = Math.Ceiling(Math.Abs(stored / powerToStoreKW));
                DateTime interuptTime = atDateTime + TimeSpan.FromSeconds(Math.Max(1, timeToEmpty));
                entity.Manager.ManagerSubpulses.AddEntityInterupt(interuptTime, nameof(EnergyGenProcessor), entity);
            }

            double load = 0;
            if (genKW > 1e-9)
            {
                double usedKW = demandKW + Math.Max(0, energyDelta > 0 ? chargeKW : 0);
                load = Math.Clamp(usedKW / genKW, 0, 1);
            }
            else if (deficitKW > 0)
            {
                load = 1;
            }

            energyGenDB.Load = load;
            energyGenDB.Output = powerToStoreKW;
            double fueluse = energyGenDB.TotalFuelUseAtMax.maxUse * load;
            energyGenDB.LocalFuel -= fueluse * Math.Max(seconds, 0);

            energyGenDB.dateTimeLastProcess = atDateTime;

            var histogram = energyGenDB.Histogram;
            int hgFirstIdx = energyGenDB.HistogramIndex;
            int hgLastIdx;
            if (hgFirstIdx == 0)
                hgLastIdx = histogram.Count - 1;
            else
                hgLastIdx = hgFirstIdx - 1;

            var hgLastObj = histogram[hgLastIdx];
            int optime = hgLastObj.seconds;

            int newoptime = (int)(optime + Math.Max(seconds, 0));

            var nexval = (foo: powerToStoreKW, demand: demandKW, store: stored, newoptime);

            if (histogram.Count < energyGenDB.HistogramSize)
                histogram.Add(nexval);
            else
            {
                histogram[hgFirstIdx] = nexval;
                if (hgFirstIdx == histogram.Count - 1)
                    energyGenDB.HistogramIndex = 0;
                else
                    energyGenDB.HistogramIndex++;
            }
        }


        internal override void ProcessEntity(Entity entity, DateTime atDateTime)
        {
            EnergyGen(entity, atDateTime);
        }
    }
}
