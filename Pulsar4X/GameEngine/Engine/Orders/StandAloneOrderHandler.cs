using Pulsar4X.Datablobs;
using Pulsar4X.Interfaces;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;
using Pulsar4X.Messaging;
using System;

namespace Pulsar4X.Engine.Orders
{
    internal class StandAloneOrderHandler : IOrderHandler
    {
        internal StandAloneOrderHandler(Game game)
        {
            Game = game;
        }

        public Game? Game { get; set; }
        public bool HandleOrder(EntityCommand entityCommand)
        {
            if (entityCommand.IsValidCommand(Game))
            {
                if (entityCommand.UseActionLanes)
                {
                    if (entityCommand.ActionOnDate > entityCommand.EntityCommanding.StarSysDateTime)
                    {
                        entityCommand.EntityCommanding.AttachedManager.ManagerSubpulses.AddEntityInterupt(entityCommand.ActionOnDate, nameof(OrderableProcessor), entityCommand.EntityCommanding);
                    }

                    if (entityCommand.EntityCommanding.TryGetDataBlob<OrderableDB>(out var orderableDB))
                    {
                        if (!orderableDB.OwningEntity.IsValid) throw new InvalidOperationException("orderableDB.OwningEntity is not valid");

                        // Issued (player) orders drop queued Standing work so Issue always wins.
                        if (entityCommand.Source == OrderSource.Issued)
                            FleetOrderCleanup.PauseStandingForPlayerIssue(orderableDB.OwningEntity);

                        orderableDB.ActionList.Add(entityCommand);

                        _ = MessagePublisher.Instance.Publish(Message.Create(
                            MessageTypes.OrdersChanged,
                            entityId: entityCommand.EntityCommanding.Id,
                            systemId: entityCommand.EntityCommanding.AttachedManager.ManagerID,
                            factionId: entityCommand.EntityCommanding.FactionOwnerID));

                        Game.ProcessorManager.GetInstanceProcessor(nameof(OrderableProcessor)).ProcessEntity(orderableDB.OwningEntity, Game.TimePulse.GameGlobalDateTime);
                    }
                }
                else
                {
                    if (entityCommand.EntityCommanding.StarSysDateTime >= entityCommand.ActionOnDate)
                        entityCommand.Execute(entityCommand.EntityCommanding.StarSysDateTime);
                    else
                    {
                        entityCommand.EntityCommanding.AttachedManager.ManagerSubpulses.AddEntityInterupt(entityCommand.ActionOnDate, nameof(OrderableProcessor), entityCommand.EntityCommanding);
                    }
                }
                return true;
            }
            return false;
        }

    }
}
