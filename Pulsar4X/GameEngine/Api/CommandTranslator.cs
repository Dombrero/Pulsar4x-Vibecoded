using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Industry;
using Pulsar4X.Industry.Orders;
using Pulsar4X.JumpPoints;
using Pulsar4X.Messaging;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Storage;
using Pulsar4X.Technology;

namespace Pulsar4X.Engine.Api
{
    /// <summary>
    /// Translates authorized API <see cref="GameCommand"/> DTOs into engine orders and dispatches them.
    /// <see cref="EngineGameServer"/> handles auth (resolving the faction/commanded entity and checking
    /// ownership) and delegates the per-command mapping here, so the command surface grows in one
    /// isolated place. Adding a command is one DTO (in Pulsar4X.Api) + one entry in <see cref="_translators"/>
    /// and a <c>Translate*</c> method. Secondary targets (a move destination, a ship to assign, …) travel
    /// as DTO fields and are resolved — and visibility-checked where applicable — here.
    /// </summary>
    internal sealed class CommandTranslator
    {
        private readonly Game _game;
        private readonly Dictionary<Type, Func<Entity, Entity, GameCommand, CommandResult>> _translators;
        public CommandTranslator(Game game)
        {
            _game = game;
            _translators = new Dictionary<Type, Func<Entity, Entity, GameCommand, CommandResult>>
            {
                [typeof(Pulsar4X.Api.RenameCommand)] = TranslateRename,
                [typeof(CreateFleetCommand)] = TranslateCreateFleet,
                [typeof(CreateColonyCommand)] = TranslateCreateColony,
                [typeof(DisbandFleetCommand)] = TranslateDisbandFleet,
                [typeof(ChangeFleetParentCommand)] = TranslateChangeFleetParent,
                [typeof(ReassignShipCommand)] = TranslateReassignShip,
                [typeof(SetFlagshipCommand)] = TranslateSetFlagship,
                [typeof(SetStandingOrdersCommand)] = TranslateSetStandingOrders,
                [typeof(MoveToBodyCommand)] = TranslateMoveToBody,
                [typeof(Pulsar4X.Api.GeoSurveyCommand)] = TranslateGeoSurvey,
                [typeof(CompleteGeoSurveyCommand)] = TranslateCompleteGeoSurvey,
                [typeof(GravSurveyCommand)] = TranslateGravSurvey,
                [typeof(Pulsar4X.Api.JumpCommand)] = TranslateJump,
                [typeof(RefuelAtCommand)] = TranslateRefuelAt,
                [typeof(AssignScientistCommand)] = TranslateAssignScientist,
                [typeof(UnassignScientistCommand)] = TranslateUnassignScientist,
                [typeof(SetResearchFundingCommand)] = TranslateSetResearchFunding,
                [typeof(AddTechToQueueCommand)] = TranslateAddTechToQueue,
                [typeof(RemoveTechFromQueueCommand)] = TranslateRemoveTechFromQueue,
                [typeof(MoveTechInQueueCommand)] = TranslateMoveTechInQueue,
                [typeof(CreateComponentDesignCommand)] = TranslateCreateComponentDesign,
                [typeof(SaveShipDesignCommand)] = TranslateSaveShipDesign,
                [typeof(DeleteShipDesignCommand)] = TranslateDeleteShipDesign,
                [typeof(SetShipDesignObsoleteCommand)] = TranslateSetShipDesignObsolete,
                [typeof(TransferCargoCommand)] = TranslateTransferCargo,
                [typeof(SetOrderPauseCommand)] = TranslateSetOrderPause,
                [typeof(Pulsar4X.Api.CancelOrderCommand)] = TranslateCancelOrder,
                [typeof(ClearFleetOrdersCommand)] = TranslateClearFleetOrders,
                [typeof(Pulsar4X.Api.NewtonThrustCommand)] = TranslateNewtonThrust,
                [typeof(Pulsar4X.Api.WarpMoveCommand)] = TranslateWarpMove,
                [typeof(SetFireControlWeaponsCommand)] = TranslateSetFireControlWeapons,
                [typeof(SetFireControlTargetCommand)] = TranslateSetFireControlTarget,
                [typeof(AssignOrdnanceCommand)] = TranslateAssignOrdnance,
                [typeof(SetFireModeCommand)] = TranslateSetFireMode,
                [typeof(UninstallComponentCommand)] = TranslateUninstallComponent,
                [typeof(InstallComponentCommand)] = TranslateInstallComponent,
                [typeof(QueueIndustryJobCommand)] = TranslateQueueIndustryJob,
                [typeof(ChangeIndustryJobPriorityCommand)] = TranslateChangeIndustryJobPriority,
                [typeof(CancelIndustryJobCommand)] = TranslateCancelIndustryJob,
                [typeof(AddToConstructionQueueCommand)] = TranslateAddToConstructionQueue,
                [typeof(MoveConstructionJobCommand)] = TranslateMoveConstructionJob,
                [typeof(RemoveConstructionJobCommand)] = TranslateRemoveConstructionJob,
            };
        }

        /// <summary>
        /// Translates and dispatches a command that has already been authorized: <paramref name="faction"/>
        /// is the requesting faction entity and <paramref name="commanded"/> the resolved, owned target.
        /// </summary>
        public CommandResult Translate(Entity faction, Entity commanded, GameCommand command)
        {
            if (!_translators.TryGetValue(command.GetType(), out var translate))
                return CommandResult.Reject($"Unsupported command: {command.GetType().Name}");

            return translate(faction, commanded, command);
        }

        // ----- helpers -----

        private CommandResult Dispatch(EntityCommand order)
            => _game.OrderHandler.HandleOrder(order)
                ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                : CommandResult.Reject("Command rejected by engine validation.");

        // The cancel/pause/standing-order paths mutate the order or standing-order list directly
        // (they have no engine order of their own), so unlike HandleOrder they must signal the
        // change themselves — otherwise the fleet UI wouldn't refresh while paused.
        private static void PublishOrdersChanged(Entity holder)
            => _ = MessagePublisher.Instance.Publish(Message.Create(
                MessageTypes.OrdersChanged,
                entityId: holder.Id,
                systemId: holder.AttachedManager.ManagerID,
                factionId: holder.FactionOwnerID));

        private bool TryResolve(int entityId, out Entity entity)
            => _game.GlobalManager.TryGetGlobalEntityById(entityId, out entity);

        /// <summary>Finds the fleet in the faction's command tree whose direct children include
        /// <paramref name="ship"/>. Returns the faction entity itself when the ship sits at the
        /// faction root, or null when the ship isn't in the tree.</summary>
        private static Entity? FindHoldingFleet(Entity fleet, Entity ship)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB)) return null;

            foreach (var child in fleetDB.GetChildren())
            {
                if (child == ship) return fleet;
                if (FindHoldingFleet(child, ship) is { } holder) return holder;
            }
            return null;
        }

        // ----- translators -----

        // Fully qualified engine order type: it shares its name with the API DTO.
        private CommandResult TranslateRename(Entity faction, Entity commanded, GameCommand command)
        {
            var rename = (Pulsar4X.Api.RenameCommand)command;
            bool accepted = Pulsar4X.Names.RenameCommand.CreateRenameCommand(_game, faction, commanded, rename.NewName);
            return accepted
                ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                : CommandResult.Reject("Command rejected by engine validation.");
        }

        private CommandResult TranslateCreateFleet(Entity faction, Entity commanded, GameCommand command)
        {
            var create = (CreateFleetCommand)command;
            if (commanded != faction)
                return CommandResult.Reject("A fleet can only be created by commanding the faction itself.");

            var system = _game.Systems.FirstOrDefault(s => s.ID == create.SystemId);
            if (system == null)
                return CommandResult.Reject($"System {create.SystemId} not found.");

            // The fleet's name is generated server-side; the client renames it afterwards if desired.
            return Dispatch(FleetOrder.CreateFleetOrder(NameFactory.GetFleetName(_game), faction, system));
        }

        private CommandResult TranslateCreateColony(Entity faction, Entity commanded, GameCommand command)
        {
            var create = (CreateColonyCommand)command;
            if (commanded != faction)
                return CommandResult.Reject("A colony can only be created by commanding the faction itself.");

            if (!TryResolve(create.BodyId, out var body))
                return CommandResult.Reject($"Entity {create.BodyId} not found.");

            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo) || factionInfo.Species.Count == 0)
                return CommandResult.Reject("The faction has no species to settle the colony with.");

            return Dispatch(Pulsar4X.Colonies.CreateColonyOrder.CreateCommand(faction, factionInfo.Species[0], body));
        }

        private CommandResult TranslateDisbandFleet(Entity faction, Entity commanded, GameCommand command)
            => Dispatch(FleetOrder.DisbandFleet(faction.Id, commanded));

        private CommandResult TranslateChangeFleetParent(Entity faction, Entity commanded, GameCommand command)
        {
            var change = (ChangeFleetParentCommand)command;
            if (!TryResolve(change.NewParentId, out var newParent))
                return CommandResult.Reject($"Entity {change.NewParentId} not found.");
            // The engine only validates the source fleet's owner, so check the new parent here.
            if (newParent.FactionOwnerID != faction.Id || !newParent.HasDataBlob<FleetDB>())
                return CommandResult.Reject("The new parent is not one of your fleets.");

            return Dispatch(FleetOrder.ChangeParent(faction.Id, commanded, newParent));
        }

        private CommandResult TranslateReassignShip(Entity faction, Entity commanded, GameCommand command)
        {
            var reassign = (ReassignShipCommand)command;

            // Reassigning to the faction root means "unattach" — only UnassignShip. AssignShip to the
            // faction would incorrectly promote the ship to faction FlagShip and transfer the faction.
            if (reassign.ToFleetId == faction.Id)
            {
                var currentHolder = FindHoldingFleet(faction, commanded);
                if (currentHolder == null || currentHolder == faction)
                    return CommandResult.Ok(Guid.NewGuid().ToString("N")); // already unattached

                return Dispatch(FleetOrder.UnassignShip(faction.Id, currentHolder, commanded));
            }

            if (!TryResolve(reassign.ToFleetId, out var toFleet))
                return CommandResult.Reject($"Entity {reassign.ToFleetId} not found.");
            if (!toFleet.HasDataBlob<FleetDB>())
                return CommandResult.Reject("Reassignment target is not a fleet.");

            // Detach from a real fleet first. Ships already on the faction root are "unattached" —
            // UnassignShip(faction, …) fails IsValidCommand (faction.FactionOwnerID != faction.Id),
            // and AssignShip already removes the ship from the faction Children list.
            var holder = FindHoldingFleet(faction, commanded);
            if (holder != null && holder != faction && holder != toFleet)
            {
                var unassign = FleetOrder.UnassignShip(faction.Id, holder, commanded);
                if (!_game.OrderHandler.HandleOrder(unassign))
                    return CommandResult.Reject("Command rejected by engine validation.");
            }

            return Dispatch(FleetOrder.AssignShip(faction.Id, toFleet, commanded));
        }

        private CommandResult TranslateSetFlagship(Entity faction, Entity commanded, GameCommand command)
        {
            var setFlagship = (SetFlagshipCommand)command;
            if (!TryResolve(setFlagship.ShipId, out var ship))
                return CommandResult.Reject($"Entity {setFlagship.ShipId} not found.");
            // The engine only validates the fleet's owner, so check the flagship ship here.
            if (ship.FactionOwnerID != faction.Id)
                return CommandResult.Reject("The flagship must be one of your own ships.");

            return Dispatch(FleetOrder.SetFlagShip(faction.Id, commanded, ship));
        }

        // The standing-orders editor runs client-side; the whole list replaces in one validated
        // write. Engine conditions/actions are rebuilt from their StandingOrderTypes ids — there is
        // no engine order for this (the pre-port UI mutated FleetDB.StandingOrders by reference).
        private CommandResult TranslateSetStandingOrders(Entity faction, Entity commanded, GameCommand command)
        {
            var set = (SetStandingOrdersCommand)command;
            if (!commanded.TryGetDataBlob<FleetDB>(out var fleetDB))
                return CommandResult.Reject("The commanded entity is not a fleet.");

            var rebuilt = new List<ConditionalOrder>();
            foreach (var order in set.Orders ?? Array.Empty<Pulsar4X.Api.StandingOrder>())
            {
                var compound = new CompoundCondition();
                var conditions = order.Conditions ?? Array.Empty<StandingOrderCondition>();
                for (int i = 0; i < conditions.Count; i++)
                {
                    var condition = conditions[i];
                    Interfaces.ICondition? engineCondition = condition.ConditionType switch
                    {
                        StandingOrderTypes.FuelCondition => new FuelCondition(
                            Math.Clamp(condition.Threshold, 0, 100), ToComparisonType(condition.Comparison)),
                        StandingOrderTypes.EnergyCondition => new EnergyCondition(
                            Math.Clamp(condition.Threshold, 0, 100), ToComparisonType(condition.Comparison)),
                        StandingOrderTypes.HealthCondition => new HealthCondition(
                            Math.Clamp(condition.Threshold, 0, 100), ToComparisonType(condition.Comparison)),
                        StandingOrderTypes.CargoFillCondition => new CargoFillCondition(
                            Math.Clamp(condition.Threshold, 0, 100), ToComparisonType(condition.Comparison)),
                        StandingOrderTypes.UnsurveyedGeoCondition => new UnsurveyedGeoCondition(
                            Math.Clamp(condition.Threshold, 0, 50), ToComparisonType(condition.Comparison)),
                        StandingOrderTypes.UnsurveyedAnomalyCondition => new UnsurveyedAnomalyCondition(
                            Math.Clamp(condition.Threshold, 0, 50), ToComparisonType(condition.Comparison)),
                        _ => null,
                    };
                    if (engineCondition == null)
                        return CommandResult.Reject($"Unknown standing-order condition: {condition.ConditionType}");

                    // Operators link a condition to the next one; the last carries none.
                    DataStructures.LogicalOperation? logic = i < conditions.Count - 1
                        ? condition.Logic == StandingOrderLogic.Or
                            ? DataStructures.LogicalOperation.Or
                            : DataStructures.LogicalOperation.And
                        : null;
                    compound.ConditionItems.Add(new ConditionItem(engineCondition, logic));
                }

                var actions = new DataStructures.SafeList<EntityCommand>();
                foreach (var actionType in order.Actions ?? Array.Empty<string>())
                {
                    // Resupply is a no-op stub — drop it so saved Move→Refuel→Resupply chains stop looping.
                    if (actionType == StandingOrderTypes.Resupply)
                        continue;

                    EntityCommand? action = actionType switch
                    {
                        StandingOrderTypes.MoveToNearestColony => MoveToNearestColonyAction.CreateCommand(faction.Id, commanded),
                        StandingOrderTypes.MoveToNearestGeoSurvey => MoveToNearestGeoSurveyAction.CreateCommand(faction.Id, commanded),
                        StandingOrderTypes.MoveToNearestGravSurvey => MoveToNearestGravSurveyAction.CreateCommand(faction.Id, commanded),
                        StandingOrderTypes.MoveToNearestAnomaly => MoveToNearestAnomalyAction.CreateCommand(faction.Id, commanded),
                        StandingOrderTypes.Refuel => RefuelAction.CreateCommand(faction.Id, commanded),
                        StandingOrderTypes.Recharge => RechargeEnergyAction.CreateCommand(faction.Id, commanded),
                        _ => null,
                    };
                    if (action == null)
                        return CommandResult.Reject($"Unknown standing-order action: {actionType}");
                    actions.Add(action);
                }

                // Refuel/Recharge already warp to the nearest colony — drop redundant Move-to-Colony.
                if (actions.Any(a => a is RefuelAction || a is RechargeEnergyAction))
                {
                    for (int i = actions.Count - 1; i >= 0; i--)
                    {
                        if (actions[i] is MoveToNearestColonyAction)
                            actions.RemoveAt(i);
                    }
                }

                EnsureDefaultStandingConditions(compound, actions);

                rebuilt.Add(new ConditionalOrder(compound, actions) { Name = order.Name ?? "" });
            }

            // Replace wholesale only after everything validated. Safe to swap live: the standing-
            // orders processor clones actions out of this list rather than executing them in place.
            fleetDB.StandingOrders.Clear();
            foreach (var order in rebuilt)
                fleetDB.StandingOrders.Add(order);
            fleetDB.ActiveStandingOrderIndex = -1;
            fleetDB.StandingSuppressUntil = null;

            PublishOrdersChanged(commanded);

            // Kick standing eval immediately so Save after Issue Idle does not wait a full hour.
            try
            {
                if (commanded.TryGetDataBlob<OrderableDB>(out var q)
                    && q != null
                    && !q.ActionList.Any(a => a.Source == OrderSource.Issued)
                    && commanded.Manager?.Game?.ProcessorManager != null)
                {
                    commanded.AttachedManager.Game.ProcessorManager
                        .RunProcessOnEntity<FleetDB>(commanded, 0);
                }
            }
            catch
            {
                // Next FleetOrderProcessor hotloop will pick it up.
            }

            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        private static void EnsureDefaultStandingConditions(
            CompoundCondition compound,
            DataStructures.SafeList<EntityCommand> actions)
        {
            if (compound.ConditionItems.Count > 0 || actions.Count == 0)
                return;

            // Players often save Grav/Geo survey with only an action — previously that never
            // ENTERed because empty conditions were skipped. Attach the natural trigger.
            if (actions.Any(a => a is MoveToNearestGravSurveyAction || a is MoveToNearestAnomalyAction))
            {
                compound.ConditionItems.Add(new ConditionItem(
                    new UnsurveyedAnomalyCondition(0f, DataStructures.ComparisonType.GreaterThan)));
                return;
            }

            if (actions.Any(a => a is MoveToNearestGeoSurveyAction))
            {
                compound.ConditionItems.Add(new ConditionItem(
                    new UnsurveyedGeoCondition(0f, DataStructures.ComparisonType.GreaterThan)));
                return;
            }

            if (actions.Any(a => a is RefuelAction))
            {
                compound.ConditionItems.Add(new ConditionItem(
                    new FuelCondition(30f, DataStructures.ComparisonType.LessThan)));
                return;
            }

            if (actions.Any(a => a is RechargeEnergyAction))
            {
                compound.ConditionItems.Add(new ConditionItem(
                    new EnergyCondition(30f, DataStructures.ComparisonType.LessThan)));
            }
        }

        private static DataStructures.ComparisonType ToComparisonType(StandingOrderComparison comparison)
            => comparison switch
            {
                StandingOrderComparison.LessThan => DataStructures.ComparisonType.LessThan,
                StandingOrderComparison.LessThanOrEqual => DataStructures.ComparisonType.LessThanOrEqual,
                StandingOrderComparison.EqualTo => DataStructures.ComparisonType.EqualTo,
                StandingOrderComparison.GreaterThan => DataStructures.ComparisonType.GreaterThan,
                _ => DataStructures.ComparisonType.GreaterThanOrEqual,
            };

        private CommandResult TranslateMoveToBody(Entity faction, Entity commanded, GameCommand command)
        {
            var move = (MoveToBodyCommand)command;
            if (!TryResolve(move.BodyId, out var body))
                return CommandResult.Reject($"Entity {move.BodyId} not found.");

            // Stuck out-of-range cargo transfers occupy the ship Movement lane and block warp.
            FleetOrderCleanup.AbortCargoTransfersOnFleetShips(commanded);

            // Goals/agent path (OrdersAndAI vertical slice) — planners emit warp/newton actions.
            AgentProcessor.AssignGoal(commanded, new Goal
            {
                Type = GoalType.MoveTo,
                TargetEntityID = body.Id,
            });
            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        private CommandResult TranslateGeoSurvey(Entity faction, Entity commanded, GameCommand command)
        {
            var survey = (Pulsar4X.Api.GeoSurveyCommand)command;
            if (!TryResolve(survey.BodyId, out var body))
                return CommandResult.Reject($"Entity {survey.BodyId} not found.");

            AgentProcessor.AssignGoal(commanded, new Goal
            {
                Type = GoalType.ServeyBodies,
                TargetEntityID = body.Id,
            });
            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        private CommandResult TranslateCompleteGeoSurvey(Entity faction, Entity commanded, GameCommand command)
        {
            if (faction.Id != _game.GameMasterFaction.Id)
                return CommandResult.Reject("Space Master only.");

            if (!commanded.TryGetDataBlob<GeoSurveyableDB>(out var geo))
                return CommandResult.Reject("Body is not geo-surveyable.");

            commanded.TryGetDataBlob<MineralsDB>(out var minerals);

            // Complete for every faction so SM and player views both see the result after toggle.
            foreach (var (fid, fac) in _game.Factions)
            {
                geo.GeoSurveyStatus[fid] = 0;
                if (minerals != null && fac.TryGetDataBlob<FactionInfoDB>(out var info))
                    minerals.GrantFactionPartialAccess(info.FactionMask);
            }

            _ = MessagePublisher.Instance.Publish(Message.Create(
                MessageTypes.EntityChanged,
                entityId: commanded.Id,
                systemId: commanded.AttachedManager.ManagerID,
                factionId: faction.Id));

            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        private CommandResult TranslateGravSurvey(Entity faction, Entity commanded, GameCommand command)
        {
            var survey = (GravSurveyCommand)command;
            if (!TryResolve(survey.LocationId, out var location))
                return CommandResult.Reject($"Entity {survey.LocationId} not found.");

            if (!_game.OrderHandler.HandleOrder(WarpFleetTowardsTargetOrder.CreateCommand(commanded, location)))
                return CommandResult.Reject("Command rejected by engine validation.");

            return Dispatch(JPSurveyOrder.CreateCommand(faction.Id, commanded, location));
        }

        private CommandResult TranslateJump(Entity faction, Entity commanded, GameCommand command)
        {
            var jump = (Pulsar4X.Api.JumpCommand)command;
            if (!TryResolve(jump.JumpPointId, out var jumpPoint)
                || !jumpPoint.TryGetDataBlob<JumpPointDB>(out var jumpPointDB))
                return CommandResult.Reject($"Jump point {jump.JumpPointId} not found.");

            // Visibility enforced at the boundary: a faction may only use jump points it has discovered.
            if (!jumpPointDB.IsDiscovered.Contains(faction.Id))
                return CommandResult.Reject("Jump point has not been discovered by the faction.");

            return JumpOrder.CreateAndExecute(_game, faction, commanded, jumpPointDB)
                ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                : CommandResult.Reject("Command rejected by engine validation.");
        }

        private CommandResult TranslateRefuelAt(Entity faction, Entity commanded, GameCommand command)
        {
            var refuel = (RefuelAtCommand)command;
            if (!TryResolve(refuel.ColonyId, out var colony))
                return CommandResult.Reject($"Entity {refuel.ColonyId} not found.");
            if (!colony.HasDataBlob<CargoStorageDB>())
                return CommandResult.Reject("Refuel target has no cargo storage.");

            // Drop stuck transfers before queuing warp + deferred refuel.
            FleetOrderCleanup.AbortCargoTransfersOnFleetShips(commanded);

            if (!FleetOrderCleanup.IsFleetAtColony(commanded, colony))
            {
                if (!_game.OrderHandler.HandleOrder(WarpFleetTowardsTargetOrder.CreateCommand(commanded, colony)))
                    return CommandResult.Reject("Command rejected by engine validation.");
            }

            // Transfers only after arrival — issuing WaitTillFull while out of range never finishes
            // and locks Movement on every ship.
            return Dispatch(RefuelWhenAtColonyOrder.CreateCommand(faction.Id, commanded, colony));
        }

        // ----- cargo transfer (commanded entity: the source) -----

        private CommandResult TranslateTransferCargo(Entity faction, Entity commanded, GameCommand command)
        {
            var transfer = (TransferCargoCommand)command;
            if (!TryResolve(transfer.PartnerEntityId, out var partner))
                return CommandResult.Reject($"Entity {transfer.PartnerEntityId} not found.");
            if (partner.FactionOwnerID != faction.Id)
                return CommandResult.Reject("The transfer partner does not belong to the faction.");
            if (!commanded.TryGetDataBlob<CargoStorageDB>(out var storage))
                return CommandResult.Reject("The commanded entity has no cargo storage.");
            if (!partner.HasDataBlob<CargoStorageDB>())
                return CommandResult.Reject("The transfer partner has no cargo storage.");
            if (transfer.Items is not { Count: > 0 })
                return CommandResult.Reject("Nothing to transfer.");

            // Items travel as cargo-item ids; resolve each against the source's stores (this also
            // covers entity-specific cargoables like component instances, which aren't in the
            // faction's goods library).
            var items = new List<(ICargoable, long)>(transfer.Items.Count);
            foreach (var (cargoItemId, units) in transfer.Items)
            {
                if (units <= 0)
                    return CommandResult.Reject("Transfer amounts must be positive.");

                ICargoable? cargoable = null;
                foreach (var store in storage.TypeStores.Values)
                {
                    if (store.GetCargoables().TryGetValue(cargoItemId, out var found))
                    {
                        cargoable = found;
                        break;
                    }
                }
                if (cargoable == null)
                    return CommandResult.Reject($"Cargo item {cargoItemId} is not in the source's storage.");

                // Negative = out of the primary (commanded) entity, per the engine's convention.
                items.Add((cargoable, -units));
            }

            return CargoTransferOrder.CreateCommands(faction.Id, commanded, partner, items)
                ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                : CommandResult.Reject("Command rejected by engine validation.");
        }

        // ----- order queue (commanded entity: the holder) -----

        // PauseOnAction is a plain flag on the queued order with no engine order of its own (the
        // pre-port UI flipped it by reference), so the translator sets it directly.
        private CommandResult TranslateSetOrderPause(Entity faction, Entity commanded, GameCommand command)
        {
            var pause = (SetOrderPauseCommand)command;
            if (!commanded.TryGetDataBlob<OrderableDB>(out var orderable))
                return CommandResult.Reject("The entity has no order queue.");

            var order = orderable.ActionList.FirstOrDefault(o => o.CmdID == pause.OrderId);
            if (order == null)
                return CommandResult.Reject($"Order {pause.OrderId} is not in the queue.");

            order.PauseOnAction = pause.Pause;
            PublishOrdersChanged(commanded);
            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        // Like SetOrderPause: removing a queued order has no engine order of its own (the pre-port
        // maneuver UI removed it from the queue by reference), so the translator does it directly.
        private CommandResult TranslateCancelOrder(Entity faction, Entity commanded, GameCommand command)
        {
            var cancel = (Pulsar4X.Api.CancelOrderCommand)command;
            if (!commanded.TryGetDataBlob<OrderableDB>(out var orderable))
                return CommandResult.Reject("The entity has no order queue.");

            var order = orderable.ActionList.FirstOrDefault(o => o.CmdID == cancel.OrderId);
            if (order == null)
                return CommandResult.Reject($"Order {cancel.OrderId} is not in the queue.");

            // Cargo transfers hold escrow on both partners — Abort restores goods and removes both orders.
            if (order is CargoTransferOrder cargoTransfer)
            {
                cargoTransfer.Abort();
                PublishOrdersChanged(commanded);
                return CommandResult.Ok(Guid.NewGuid().ToString("N"));
            }

            // Allow cancelling running orders too — otherwise stub/stuck actions can lock a fleet forever.
            orderable.ActionList.Remove(order);
            PublishOrdersChanged(commanded);
            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        private CommandResult TranslateClearFleetOrders(Entity faction, Entity commanded, GameCommand command)
        {
            if (!commanded.HasDataBlob<FleetDB>())
                return CommandResult.Reject("ClearFleetOrders requires a fleet.");

            FleetOrderCleanup.ClearFleetAndShipOrders(commanded);
            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        // ----- ship movement (commanded entity: the ship) -----

        private CommandResult TranslateNewtonThrust(Entity faction, Entity commanded, GameCommand command)
        {
            var thrust = (Pulsar4X.Api.NewtonThrustCommand)command;
            if (!commanded.TryGetDataBlob<Pulsar4X.Movement.NewtonThrustAbilityDB>(out var thrustAbility))
                return CommandResult.Reject("The entity has no newtonian thrust ability.");
            if (!commanded.TryGetDataBlob<MassVolumeDB>(out var massVolume))
                return CommandResult.Reject("The entity has no mass data.");

            var deltaV = new Pulsar4X.Orbital.Vector3(thrust.DeltaVMps.X, thrust.DeltaVMps.Y, thrust.DeltaVMps.Z);
            if (deltaV.Length() <= 0)
                return CommandResult.Reject("The burn has no ΔV.");
            if (deltaV.Length() > thrustAbility.DeltaV)
                return CommandResult.Reject("The burn exceeds the ship's available ΔV.");

            double fuelBurned = Pulsar4X.Orbital.OrbitalMath.TsiolkovskyFuelUse(
                massVolume.MassTotal, thrustAbility.ExhaustVelocity, deltaV.Length());
            double burnSeconds = thrustAbility.FuelBurnRate > 0 ? fuelBurned / thrustAbility.FuelBurnRate : 0;

            return Dispatch(Pulsar4X.Movement.NewtonThrustCommand.CreateCommand(
                faction.Id, commanded, thrust.NodeTime, deltaV, burnSeconds));
        }

        private CommandResult TranslateWarpMove(Entity faction, Entity commanded, GameCommand command)
        {
            var warp = (Pulsar4X.Api.WarpMoveCommand)command;
            if (!TryResolve(warp.DestinationId, out var destination))
                return CommandResult.Reject($"Entity {warp.DestinationId} not found.");

            // Visibility enforced at the boundary: a faction can only warp to what it can see.
            if (destination.Manager == null
                || !destination.AttachedManager.IsEntityVisibleToFaction(destination, faction.Id))
                return CommandResult.Reject($"Entity {warp.DestinationId} not found.");

            DateTime now = commanded.StarSysDateTime;
            try
            {
                if (warp.InsertionPointRelative is { } insertion)
                    return Pulsar4X.Movement.WarpMoveCommand.CreateCommand(commanded, destination, now,
                            new Pulsar4X.Orbital.Vector3(insertion.X, insertion.Y, insertion.Z))
                        ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                        : CommandResult.Reject("Command rejected by engine validation.");

                return Dispatch(Pulsar4X.Movement.WarpMoveCommand.CreateCommandEZ(commanded, destination, now));
            }
            catch (Exception e)
            {
                // The intercept/insertion math throws on movement states it can't predict
                // (e.g. an entity with no velocity); reject rather than crash the server.
                return CommandResult.Reject($"Warp could not be plotted: {e.Message}");
            }
        }

        // ----- fire control (commanded entity: the ship) -----

        private CommandResult TranslateSetFireControlWeapons(Entity faction, Entity commanded, GameCommand command)
        {
            var assign = (SetFireControlWeaponsCommand)command;
            bool accepted = Pulsar4X.Weapons.SetWeaponsFireControlOrder.CreateCommand(
                _game, commanded.StarSysDateTime, faction.Id, commanded.Id,
                assign.FireControlId, assign.WeaponIds.ToList());
            return accepted
                ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                : CommandResult.Reject("Command rejected by engine validation.");
        }

        private CommandResult TranslateSetFireControlTarget(Entity faction, Entity commanded, GameCommand command)
        {
            var target = (SetFireControlTargetCommand)command;
            if (!TryResolve(target.TargetId, out var targetEntity))
                return CommandResult.Reject($"Entity {target.TargetId} not found.");
            // A faction can only target what it can see (no locking onto undetected entities).
            if (targetEntity.Manager == null
                || !targetEntity.AttachedManager.IsEntityVisibleToFaction(targetEntity, faction.Id))
                return CommandResult.Reject($"Entity {target.TargetId} not found.");

            bool accepted = Pulsar4X.Weapons.SetTargetFireControlOrder.CreateCommand(
                _game, commanded.StarSysDateTime, faction.Id, commanded.Id,
                target.FireControlId, target.TargetId);
            return accepted
                ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                : CommandResult.Reject("Command rejected by engine validation.");
        }

        private CommandResult TranslateAssignOrdnance(Entity faction, Entity commanded, GameCommand command)
        {
            var assign = (AssignOrdnanceCommand)command;
            bool accepted = Pulsar4X.Weapons.SetOrdinanceToWpnOrder.CreateCommand(
                _game, commanded.StarSysDateTime, faction.Id, commanded.Id,
                assign.WeaponId, assign.OrdnanceDesignId);
            return accepted
                ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                : CommandResult.Reject("Command rejected by engine validation.");
        }

        private CommandResult TranslateSetFireMode(Entity faction, Entity commanded, GameCommand command)
        {
            var mode = (SetFireModeCommand)command;
            bool accepted = Pulsar4X.Weapons.SetOpenFireControlOrder.CreateCmd(
                _game, faction.Id, commanded.Id, mode.FireControlId,
                mode.OpenFire
                    ? Pulsar4X.Weapons.SetOpenFireControlOrder.FireModes.OpenFire
                    : Pulsar4X.Weapons.SetOpenFireControlOrder.FireModes.CeaseFire);
            return accepted
                ? CommandResult.Ok(Guid.NewGuid().ToString("N"))
                : CommandResult.Reject("Command rejected by engine validation.");
        }

        // ----- research (commanded entity: the lab) -----

        private CommandResult TranslateAssignScientist(Entity faction, Entity commanded, GameCommand command)
        {
            var assign = (AssignScientistCommand)command;
            if (!TryResolve(assign.ScientistId, out var scientist)
                || scientist.FactionOwnerID != faction.Id)
                return CommandResult.Reject($"Scientist {assign.ScientistId} not found.");

            return Dispatch(AssignScientistOrder.Create(commanded, assign.ScientistId));
        }

        private CommandResult TranslateUnassignScientist(Entity faction, Entity commanded, GameCommand command)
        {
            var unassign = (UnassignScientistCommand)command;
            return Dispatch(UnassignScientistOrder.Create(commanded, unassign.ScientistId));
        }

        private CommandResult TranslateSetResearchFunding(Entity faction, Entity commanded, GameCommand command)
        {
            var funding = (SetResearchFundingCommand)command;
            if (funding.FundingLevel is < 0 or > 5)
                return CommandResult.Reject("Funding level must be between 0 and 5.");

            return Dispatch(FundingChangedOrder.Create(commanded, (byte)funding.FundingLevel));
        }

        private CommandResult TranslateAddTechToQueue(Entity faction, Entity commanded, GameCommand command)
        {
            var add = (AddTechToQueueCommand)command;
            return Dispatch(AddTechToQueueOrder.Create(commanded, add.TechId));
        }

        private CommandResult TranslateRemoveTechFromQueue(Entity faction, Entity commanded, GameCommand command)
        {
            var remove = (RemoveTechFromQueueCommand)command;
            return Dispatch(RemoveTechFromQueueOrder.Create(commanded, remove.TechId));
        }

        private CommandResult TranslateMoveTechInQueue(Entity faction, Entity commanded, GameCommand command)
        {
            var move = (MoveTechInQueueCommand)command;
            return move.MoveUp
                ? Dispatch(MoveUpInQueueOrder.Create(commanded, move.TechId))
                : Dispatch(MoveDownInQueueOrder.Create(commanded, move.TechId));
        }

        // ----- component design (commanded entity: the faction itself) -----

        private CommandResult TranslateCreateComponentDesign(Entity faction, Entity commanded, GameCommand command)
        {
            var create = (CreateComponentDesignCommand)command;
            if (commanded != faction)
                return CommandResult.Reject("A component design can only be created by commanding the faction itself.");

            if (string.IsNullOrWhiteSpace(create.Name))
                return CommandResult.Reject("The design needs a name.");

            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo)
                || !factionInfo.Data.ComponentTemplates.TryGetValue(create.TemplateId, out var template))
                return CommandResult.Reject($"Component template {create.TemplateId} not found.");

            if (!faction.TryGetDataBlob<FactionTechDB>(out var factionTech))
                return CommandResult.Reject("The faction has no tech state.");

            try
            {
                // Like the research instant orders, design creation is a direct faction-data write
                // (no engine order exists): replay the inputs onto a fresh designer and finalise it,
                // which also registers the design's research project.
                var designer = DesignerInputs.Build(factionInfo.Data, factionTech, template,
                    create.Inputs ?? Array.Empty<DesignerInput>());
                designer.Name = create.Name;
                designer.CreateDesign(faction);
            }
            catch (Exception e)
            {
                // Formula/attribute evaluation runs over moddable data; reject rather than crash.
                return CommandResult.Reject($"Design creation failed: {e.Message}");
            }

            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        // ----- ship design (commanded entity: the faction itself) -----

        private CommandResult TranslateSaveShipDesign(Entity faction, Entity commanded, GameCommand command)
        {
            var save = (SaveShipDesignCommand)command;
            if (commanded != faction)
                return CommandResult.Reject("A ship design can only be saved by commanding the faction itself.");

            if (string.IsNullOrWhiteSpace(save.Name))
                return CommandResult.Reject("The design needs a name.");

            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo))
                return CommandResult.Reject("The faction has no data store.");

            if (!factionInfo.Data.Armor.TryGetValue(save.ArmorId, out var armor))
                return CommandResult.Reject($"Armor {save.ArmorId} not found.");

            if (save.ArmorThickness < 0)
                return CommandResult.Reject("Armor thickness cannot be negative.");

            // Resolve the component stacks against the faction's own designs — referencing another
            // faction's (or an unknown) component rejects here at the boundary.
            var components = new List<(ComponentDesign design, int count)>(save.Components?.Count ?? 0);
            foreach (var entry in save.Components ?? Array.Empty<ShipComponentCount>())
            {
                if (!factionInfo.ComponentDesigns.TryGetValue(entry.ComponentDesignId, out var componentDesign))
                    return CommandResult.Reject($"Component design {entry.ComponentDesignId} not found.");
                if (entry.Count < 0)
                    return CommandResult.Reject("Component counts cannot be negative.");
                components.Add((componentDesign, entry.Count));
            }

            try
            {
                Pulsar4X.Ships.ShipDesign design;
                if (!string.IsNullOrEmpty(save.DesignId))
                {
                    if (!factionInfo.ShipDesigns.TryGetValue(save.DesignId, out design!))
                        return CommandResult.Reject($"Ship design {save.DesignId} not found.");

                    design.Name = save.Name;
                    design.Components = components;
                    design.Armor = (armor, save.ArmorThickness);
                }
                else
                {
                    design = new Pulsar4X.Ships.ShipDesign(factionInfo, save.Name, components, (armor, save.ArmorThickness));
                }

                design.IsObsolete = save.IsObsolete;
                // Recalculates the derived values and (re-)registers the design on the faction.
                design.Initialise(factionInfo);
                design.IsValid = !design.IsObsolete && IsShipDesignValid(design);
            }
            catch (Exception e)
            {
                // The damage-profile/armor math runs over moddable data; reject rather than crash.
                return CommandResult.Reject($"Ship design save failed: {e.Message}");
            }

            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        // Mirrors the designer UI's validity rule: a producible ship needs mass, newtonian thrust,
        // and energy generation + storage.
        private static bool IsShipDesignValid(Pulsar4X.Ships.ShipDesign design)
        {
            bool hasThrust = false, hasEnergyGen = false, hasEnergyStore = false;
            foreach (var (componentDesign, count) in design.Components)
            {
                if (count <= 0) continue;
                hasThrust |= componentDesign.HasAttribute<NewtonionThrustAtb>();
                hasEnergyGen |= componentDesign.HasAttribute<Pulsar4X.Energy.EnergyGenerationAtb>();
                hasEnergyStore |= componentDesign.HasAttribute<Pulsar4X.Energy.EnergyStoreAtb>();
            }

            return design.MassPerUnit > 0 && hasThrust && hasEnergyGen && hasEnergyStore;
        }

        private CommandResult TranslateDeleteShipDesign(Entity faction, Entity commanded, GameCommand command)
        {
            var delete = (DeleteShipDesignCommand)command;
            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo)
                || !factionInfo.ShipDesigns.Remove(delete.DesignId))
                return CommandResult.Reject($"Ship design {delete.DesignId} not found.");

            factionInfo.IndustryDesigns.Remove(delete.DesignId);
            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        private CommandResult TranslateSetShipDesignObsolete(Entity faction, Entity commanded, GameCommand command)
        {
            var obsolete = (SetShipDesignObsoleteCommand)command;
            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo)
                || !factionInfo.ShipDesigns.TryGetValue(obsolete.DesignId, out var design))
                return CommandResult.Reject($"Ship design {obsolete.DesignId} not found.");

            design.IsObsolete = true;
            design.IsValid = false;
            return CommandResult.Ok(Guid.NewGuid().ToString("N"));
        }

        // ----- installations/components (commanded entity: the colony or ship holding them) -----

        private CommandResult TranslateUninstallComponent(Entity faction, Entity commanded, GameCommand command)
        {
            var uninstall = (UninstallComponentCommand)command;
            if (!commanded.TryGetDataBlob<ComponentInstancesDB>(out var instancesDB)
                || !instancesDB.ComponentsByDesign.TryGetValue(uninstall.DesignId, out var instances)
                || instances.Count == 0)
                return CommandResult.Reject($"No installed component of design {uninstall.DesignId}.");

            var instance = instances[0];
            if (!commanded.TryGetDataBlob<Pulsar4X.Storage.CargoStorageDB>(out var storage)
                || !storage.TypeStores.ContainsKey(instance.CargoTypeID))
                return CommandResult.Reject("The entity has no cargo storage that can hold the component.");

            if (!_game.OrderHandler.HandleOrder(UninstallComponentInstanceOrder.Create(commanded, instance)))
                return CommandResult.Reject("Command rejected by engine validation.");

            return Dispatch(AddComponentToStorageOrder.Create(commanded, instance));
        }

        private CommandResult TranslateInstallComponent(Entity faction, Entity commanded, GameCommand command)
        {
            var install = (InstallComponentCommand)command;

            // The component travels as a cargo-item id; find the live instance in the holder's storage.
            ComponentInstance? instance = null;
            if (commanded.TryGetDataBlob<Pulsar4X.Storage.CargoStorageDB>(out var storage))
            {
                foreach (var typeStore in storage.TypeStores.Values)
                {
                    if (typeStore.CurrentStoreInUnits.ContainsKey(install.ComponentId)
                        && typeStore.GetCargoables().TryGetValue(install.ComponentId, out var cargoable)
                        && cargoable is ComponentInstance found)
                    {
                        instance = found;
                        break;
                    }
                }
            }

            if (instance == null)
                return CommandResult.Reject($"Component {install.ComponentId} is not in the entity's storage.");

            if (!_game.OrderHandler.HandleOrder(RemoveComponentFromStorageOrder.Create(commanded, instance)))
                return CommandResult.Reject("Command rejected by engine validation.");

            return Dispatch(InstallComponentInstanceOrder.Create(commanded, instance));
        }

        // ----- industry / local construction (commanded entity: the colony) -----

        // IndustryTools index ProductionLines unguarded and IndustryOrder2 runs synchronously, so a
        // bad/stale line id would throw out of SubmitCommand rather than reject. Validate it here.
        private CommandResult? ValidateIndustryLine(Entity commanded, string productionLineId)
        {
            if (!commanded.TryGetDataBlob<IndustryAbilityDB>(out var industryDB))
                return CommandResult.Reject("The commanded entity has no industry.");
            if (!industryDB.ProductionLines.ContainsKey(productionLineId))
                return CommandResult.Reject($"Production line {productionLineId} not found.");
            return null;
        }

        private CommandResult TranslateQueueIndustryJob(Entity faction, Entity commanded, GameCommand command)
        {
            var queue = (QueueIndustryJobCommand)command;
            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo)
                || !factionInfo.IndustryDesigns.TryGetValue(queue.DesignId, out var design))
                return CommandResult.Reject($"Design {queue.DesignId} not found.");

            if (queue.Quantity is < 1 or > ushort.MaxValue)
                return CommandResult.Reject("Quantity must be between 1 and 65535.");

            if (ValidateIndustryLine(commanded, queue.ProductionLineId) is { } lineError)
                return lineError;

            var industryDB = commanded.GetDataBlob<IndustryAbilityDB>();
            var line = industryDB.ProductionLines[queue.ProductionLineId];
            if (!line.IndustryTypeRates.ContainsKey(design.IndustryTypeID))
                return CommandResult.Reject("This production line cannot produce that design.");

            // Ship yards assemble hulls only; components are built at Factories.
            if (line.IndustryTypeRates.ContainsKey("ship-assembly") && design is not Pulsar4X.Ships.ShipDesign)
                return CommandResult.Reject("Ship yards can only assemble ship designs. Build components at a Factory.");

            var job = new IndustryJob(factionInfo, queue.DesignId);

            // Auto-install only for colony installations (PlanetInstallation, not ShipComponent).
            if (queue.AutoInstall
                && design is Pulsar4X.Components.ComponentDesign componentDesign
                && IndustryTools.IsColonyInstallationDesign(componentDesign))
            {
                job.InstallOn = commanded;
            }

            job.InitialiseJob((ushort)queue.Quantity, queue.Repeat);
            return Dispatch(IndustryOrder2.CreateNewJobOrder(faction.Id, commanded, queue.ProductionLineId, job));
        }

        private CommandResult TranslateChangeIndustryJobPriority(Entity faction, Entity commanded, GameCommand command)
        {
            var move = (ChangeIndustryJobPriorityCommand)command;
            if (ValidateIndustryLine(commanded, move.ProductionLineId) is { } lineError)
                return lineError;
            return Dispatch(IndustryOrder2.CreateChangePriorityOrder(
                faction.Id, commanded, move.ProductionLineId, move.JobId, (short)move.Delta));
        }

        private CommandResult TranslateCancelIndustryJob(Entity faction, Entity commanded, GameCommand command)
        {
            var cancel = (CancelIndustryJobCommand)command;
            if (ValidateIndustryLine(commanded, cancel.ProductionLineId) is { } lineError)
                return lineError;
            return Dispatch(IndustryOrder2.CreateCancelJobOrder(
                faction.Id, commanded, cancel.ProductionLineId, cancel.JobId));
        }

        private CommandResult TranslateAddToConstructionQueue(Entity faction, Entity commanded, GameCommand command)
        {
            var add = (AddToConstructionQueueCommand)command;
            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo)
                || !factionInfo.ComponentDesigns.TryGetValue(add.DesignId, out var design))
                return CommandResult.Reject($"Design {add.DesignId} not found.");

            return Dispatch(AddToConstructionQueueOrder.Create(commanded, design));
        }

        private CommandResult TranslateMoveConstructionJob(Entity faction, Entity commanded, GameCommand command)
        {
            var move = (MoveConstructionJobCommand)command;
            if (!TryGetConstructionJob(commanded, move.QueueIndex, out var job))
                return CommandResult.Reject($"No construction job at queue position {move.QueueIndex}.");

            return move.MoveUp
                ? Dispatch(MoveUpInConstructionQueueOrder.Create(commanded, job))
                : Dispatch(MoveDownInConstructionQueueOrder.Create(commanded, job));
        }

        private CommandResult TranslateRemoveConstructionJob(Entity faction, Entity commanded, GameCommand command)
        {
            var remove = (RemoveConstructionJobCommand)command;
            if (!TryGetConstructionJob(commanded, remove.QueueIndex, out var job))
                return CommandResult.Reject($"No construction job at queue position {remove.QueueIndex}.");

            return Dispatch(RemoveFromConstructionQueueOrder.Create(commanded, job));
        }

        // Local-construction jobs carry no id, so commands address them by queue position.
        private static bool TryGetConstructionJob(Entity entity, int queueIndex, out LocalConstructionJob? job)
        {
            job = null;
            if (!entity.TryGetDataBlob<LocalConstructionDB>(out var construction)) return false;
            if (queueIndex < 0 || queueIndex >= construction.BuildQueue.Count) return false;

            job = construction.BuildQueue.ElementAt(queueIndex);
            return true;
        }
    }
}
