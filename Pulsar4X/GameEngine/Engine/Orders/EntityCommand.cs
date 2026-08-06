using System;
using Newtonsoft.Json;
using Pulsar4X.Datablobs;
using Pulsar4X.Interfaces;

namespace Pulsar4X.Engine.Orders
{
    /// <summary>
    /// Where a fleet/ship order came from. Issued (player Issue Orders) always outranks Standing.
    /// </summary>
    public enum OrderSource
    {
        Issued = 0,
        Standing = 1,
    }

    public enum ActionStatus
    {
        Queued,
        Running,
        Succeeded,
        Failed,
    }

    public abstract class EntityCommand
    {
        [Flags]
        public enum ActionLaneTypes
        {
            InstantOrder = 0,
            Movement = 1,
            InteractWithExternalEntity = 2,
            InteractWithEntitySameFleet = 4,
            InteractWithSelf = 8,
        }

        [JsonProperty]
        public string CmdID { get; internal set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Id of the Goal that spawned this action ("" if issued directly by a player).
        /// </summary>
        [JsonProperty]
        public string ParentGoalId { get; set; } = "";

        /// <summary>Outcome for the goals/agent layer; kept in sync by OrderableProcessor.</summary>
        [JsonProperty]
        public ActionStatus Status { get; set; } = ActionStatus.Queued;

        public bool UseActionLanes = true;
        public abstract ActionLaneTypes ActionLanes { get; }
        public abstract bool IsBlocking { get; }
        public abstract string Name { get; }
        public abstract string Details { get; }

        /// <summary>Player Issue Orders vs Standing Orders. Default is Issued.</summary>
        [JsonProperty]
        public OrderSource Source { get; set; } = OrderSource.Issued;

        public virtual void UpdateDetailString()
        { }

        [JsonProperty]
        /// <summary>
        /// This is the faction that has requested the command.
        /// </summary>
        /// <value>The requesting faction GUID.</value>
        internal int RequestingFactionGuid { get; set; }
        [JsonProperty]
        /// <summary>
        /// The Entity this command is targeted at
        /// </summary>
        /// <value>The entity GUID.</value>
        internal int EntityCommandingGuid { get; set; }

        [JsonProperty]
        /// <summary>
        /// Gets or sets the datetime this command was created by the player/client.
        /// </summary>
        /// <value>The created date.</value>
        public DateTime CreatedDate { get; set; }

        /// <summary>
        /// This sets the datetime that the order should be actioned on (ie delayed from creation)
        ///
        /// </summary>
        [JsonProperty]
        public DateTime ActionOnDate { get; set; }

        [JsonProperty]
        /// <summary>
        /// Gets or sets the datetime this command was actioned/processed by the server.
        /// this may be needed by the client to ensure it stays in synch with the server.
        /// </summary>
        /// <value>The actioned on date.</value>
        public DateTime ActionedOnDate { get; set; }


        internal abstract Entity EntityCommanding { get; }

        /// <summary>
        /// checks that the entities exsist and that the entity is owned by the faction.
        /// may eventualy need to return a responce instead of just bool.
        /// </summary>
        internal abstract bool IsValidCommand(Game game);
        /// <summary>
        /// Actions the command.
        /// </summary>
        /// <param name="game">Game.</param>
        internal abstract void Execute(DateTime atDateTime);

        public bool PauseOnAction = false;

        public bool IsRunning { get; protected set; } = false;
        internal abstract bool IsFinished();
        [JsonProperty]
        protected bool _isFinished = false;
        public bool GetIsFinished { get { return _isFinished; } }

        public abstract EntityCommand Clone();

        /// <summary>
        /// Standing-order templates keep <see cref="EntityCommandingGuid"/> across save/load but
        /// drop the live <see cref="Entity"/> reference. Rebind before <see cref="Clone"/> /
        /// <see cref="Execute"/>.
        /// </summary>
        internal virtual void BindCommandingEntity(Entity entity)
        {
            EntityCommandingGuid = entity.Id;
        }
    }

    public static class CommandHelpers
    {
        public static bool IsCommandValid(EntityManager globalManager, int factionId, int targetEntityId, out Entity factionEntity, out Entity targetEntity)
        {
            if (globalManager.TryGetGlobalEntityById(targetEntityId, out targetEntity))
            {
                if (globalManager.Game.Factions.ContainsKey(factionId))
                {
                    factionEntity = globalManager.Game.Factions[factionId];
                    if (targetEntity.FactionOwnerID == factionEntity.Id)
                        return true;
                }
            }
            factionEntity = Entity.InvalidEntity;
            return false;
        }
    }

    public class CommandReferences
    {
        internal int FactionId;
        internal int EntityId;
        public IOrderHandler? Handler;
        private ManagerSubPulse? _subPulse;
        internal DateTime GetSystemDatetime { get { return _subPulse.StarSysDateTime; } }

        internal CommandReferences(int faction, int entity, IOrderHandler handler, ManagerSubPulse subPulse)
        {
            FactionId = faction;
            EntityId = entity;
            Handler = handler;
            _subPulse = subPulse;
        }

        public static CommandReferences CreateForEntity(Game game, Entity entity)
        {
            return new CommandReferences(entity.FactionOwnerID, entity.Id, game.OrderHandler, entity.AttachedManager.ManagerSubpulses);
        }

        public static CommandReferences CreateForEntity(Game game, int entityId)
        {
            Entity entity;
            if (game.GlobalManager.TryGetEntityById(entityId, out entity))
                return new CommandReferences(entity.FactionOwnerID, entityId, game.OrderHandler, entity.AttachedManager.ManagerSubpulses);
            else
                throw new Exception("Entity Not Found");
        }
    }
}
