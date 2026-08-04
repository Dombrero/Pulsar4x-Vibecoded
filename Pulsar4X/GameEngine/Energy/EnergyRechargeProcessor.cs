using System;
using Pulsar4X.Engine;
using Pulsar4X.Interfaces;

namespace Pulsar4X.Energy
{
    public class EnergyRechargeProcessor : IHotloopProcessor
    {
        public TimeSpan RunFrequency => TimeSpan.FromMinutes(1);
        public TimeSpan FirstRunOffset => TimeSpan.FromSeconds(5);
        public Type GetParameterType => typeof(EnergyRechargeDB);

        public void Init(Game game) { }

        public int ProcessManager(EntityManager manager, int deltaSeconds)
        {
            var entities = manager.GetAllEntitiesWithDataBlob<EnergyRechargeDB>();
            foreach (var entity in entities)
                ProcessEntity(entity, deltaSeconds);
            return entities.Count;
        }

        public void ProcessEntity(Entity entity, int deltaSeconds)
        {
            if (!entity.TryGetDataBlob<EnergyRechargeDB>(out var recharge))
                return;

            var colony = recharge.ColonyEntity;
            if (colony == null || !colony.IsValid)
            {
                if (entity.AttachedManager.TryGetEntityById(recharge.ColonyEntityId, out var resolved))
                {
                    colony = resolved;
                    recharge.ColonyEntity = resolved;
                }
                else
                {
                    entity.RemoveDataBlob<EnergyRechargeDB>();
                    return;
                }
            }

            if (!EnergyRechargeHelper.ShipNeedsEnergy(entity))
            {
                entity.RemoveDataBlob<EnergyRechargeDB>();
                return;
            }

            if (!colony.TryGetDataBlob<ColonyPowerDB>(out var power) || power.SpendableKJ <= 0)
            {
                entity.RemoveDataBlob<EnergyRechargeDB>();
                return;
            }

            // Always recompute — frozen RateKW can be stale after redesigns / Recalc.
            double rate = EnergyRechargeHelper.GetEffectiveRechargeRateKW(colony, entity);
            recharge.RateKW = rate;
            if (rate <= 0)
                return;

            // Cap catch-up dumps: one process step should not apply more than a short window
            // even if the system jumped far ahead in a single interrupt.
            double seconds = Math.Clamp(deltaSeconds, 0, RunFrequency.TotalSeconds * 2.0);
            EnergyRechargeHelper.TransferEnergy(colony, entity, rate, seconds);

            if (!EnergyRechargeHelper.ShipNeedsEnergy(entity))
                entity.RemoveDataBlob<EnergyRechargeDB>();
        }
    }
}
