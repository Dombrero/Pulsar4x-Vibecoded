using Pulsar4X.Datablobs;
using Pulsar4X.Interfaces;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;
using Pulsar4X.Messaging;
using System;
using System.Linq;

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
                            DropStandingOrders(orderableDB);

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

        /// <summary>
        /// Remove standing-sourced fleet work when the player issues a new order.
        /// Always clears standing commitment — even if the standing queue was already empty —
        /// so Standing can re-enter cleanly once the Issued queue drains.
        /// </summary>
        private static void DropStandingOrders(OrderableDB orderableDB)
        {
            orderableDB.ActionList.RemoveAll(a => a.Source == OrderSource.Standing);

            var entity = orderableDB.OwningEntity;
            if (entity == null || !entity.TryGetDataBlob<FleetDB>(out var fleetDB))
                return;

            // Without this, Issue while standing had drained its queue (but kept commitment)
            // left ActiveStandingOrderIndex stuck; after the Issue finished Standing could
            // sit Idle forever behind a stale Refuel/busy gate.
            fleetDB.ActiveStandingOrderIndex = -1;
            fleetDB.StandingSuppressUntil = null;
            FleetOrderCleanup.AbortCargoTransfersOnFleetShips(entity);
            FleetOrderCleanup.AbortShipMovementOrders(entity);
        }
    }
}
