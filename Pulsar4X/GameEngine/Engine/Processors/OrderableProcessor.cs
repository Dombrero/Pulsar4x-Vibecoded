using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Fleets;
using Pulsar4X.Interfaces;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Events;
using Pulsar4X.Ships;

namespace Pulsar4X.Engine
{

    public class OrderableProcessor : IInstanceProcessor, IHotloopProcessor
    {
        public TimeSpan RunFrequency => TimeSpan.FromMinutes(10);

        public TimeSpan FirstRunOffset => TimeSpan.FromMinutes(5);

        public Type GetParameterType => typeof(OrderableDB);

        private Game? _game;

        public void Init(Game game)
        {
            _game = game;
        }

        public void ProcessEntity(Entity entity, int deltaSeconds)
        {
            DateTime atDateTime = entity.StarSysDateTime + TimeSpan.FromSeconds(deltaSeconds);
            ProcessEntity(entity, atDateTime);
        }

        public int ProcessManager(EntityManager manager, int deltaSeconds)
        {
            List<Entity> entitysWithCargoTransfers = manager.GetAllEntitiesWithDataBlob<OrderableDB>();

            foreach (var entity in entitysWithCargoTransfers)
            {
                ProcessEntity(entity, deltaSeconds);
            }

            return entitysWithCargoTransfers.Count;
        }

        internal override void ProcessEntity(Entity entity, DateTime atDateTime)
        {
            if (entity.TryGetDataBlob<OrderableDB>(out var orderableDB))
            {
                bool hadIssued = orderableDB.ActionList.Any(a => a.Source == OrderSource.Issued);
                int mask = 0;
                int finishedCount = 0;

                // Snapshot — Execute may insert/remove orders (e.g. Refuel follow-ups).
                var commands = orderableDB.ActionList.ToList();
                foreach (var entityCommand in commands)
                {
                    if (!orderableDB.ActionList.Contains(entityCommand))
                        continue;

                    if ((mask & ((int)entityCommand.ActionLanes)) == 0) //bitwise and
                    {
                        if (entityCommand.IsBlocking)
                        {
                            mask = mask | ((int)entityCommand.ActionLanes); //bitwise or
                        }
                        if (atDateTime >= entityCommand.ActionOnDate)
                        {
                            if (entityCommand.PauseOnAction & !entityCommand.IsRunning)
                            {
                                var e = Event.Create(EventType.OrdersHalt,
                                                        atDateTime,
                                                        "",
                                                        entityCommand.RequestingFactionGuid,
                                                        entityCommand.EntityCommanding.AttachedManager.ManagerID,
                                                        entityCommand.EntityCommandingGuid);
                                EventManager.Instance.Publish(e);
                            }
                            try
                            {
                                entityCommand.Execute(atDateTime);
                                if (entityCommand.IsRunning && entityCommand.Status == ActionStatus.Queued)
                                    entityCommand.Status = ActionStatus.Running;
                            }
                            catch (Exception ex)
                            {
                                // A single bad order must not tear down the sim / client.
                                DebugTraceLog.Error("Orders",
                                    $"'{entityCommand.Name}' failed on entity#{entity.Id}: {ex.Message}",
                                    atDateTime);
                                System.Diagnostics.Debug.WriteLine(
                                    $"Order '{entityCommand.Name}' failed on entity {entity.Id}: {ex}");
                                entityCommand.Status = ActionStatus.Failed;
                                orderableDB.ActionList.Remove(entityCommand);
                                finishedCount++;
                                if (!string.IsNullOrEmpty(entityCommand.ParentGoalId))
                                    AgentProcessor.RunAgentNow(entity);
                                continue;
                            }
                        }
                    }
                }

                bool finishedGoalWork = false;
                try
                {
                    orderableDB.ActionList.RemoveAll(e =>
                    {
                        try
                        {
                            if (!e.IsFinished())
                                return false;
                            if (e.Status != ActionStatus.Failed)
                                e.Status = ActionStatus.Succeeded;
                            if (!string.IsNullOrEmpty(e.ParentGoalId))
                                finishedGoalWork = true;
                            finishedCount++;
                            return true;
                        }
                        catch
                        {
                            e.Status = ActionStatus.Failed;
                            if (!string.IsNullOrEmpty(e.ParentGoalId))
                                finishedGoalWork = true;
                            finishedCount++;
                            return true;
                        }
                    });
                }
                catch
                {
                    // Ignore cleanup failures.
                }

                if (finishedGoalWork)
                {
                    try { AgentProcessor.RunAgentNow(entity); }
                    catch { /* agent wake is best-effort */ }
                }

                // Standing is event-driven: wake when any order finishes (Issued/Standing/ship
                // cargo/warp), including when a fleet child's queue drains.
                bool issuedCleared = hadIssued
                    && !orderableDB.ActionList.Any(a => a.Source == OrderSource.Issued);
                if (finishedCount > 0 || issuedCleared)
                {
                    try
                    {
                        if (entity.HasDataBlob<ShipInfoDB>())
                            ShipStandingDirector.TryStepNow(entity);
                        else if (entity.HasDataBlob<FleetDB>())
                            ShipStandingDirector.KickIdleChildren(entity);

                        FleetOrderProcessor.TryEvaluateNow(entity);
                    }
                    catch
                    {
                        // Standing will resume on the next FleetOrderProcessor safety poll.
                    }
                }
            }
        }
    }
}
