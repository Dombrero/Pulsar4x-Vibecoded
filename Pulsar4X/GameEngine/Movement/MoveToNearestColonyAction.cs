using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;

namespace Pulsar4X.Movement
{
    public class MoveToNearestColonyAction : MoveToNearestAction
    {
        public override string Name => "Move to Nearest Colony";
        public override string Details => "Moves the fleet to the nearest colony.";
        private static bool ColonyFilter(Entity entity)
        {
            return entity.HasDataBlob<ColonyInfoDB>();
        }

        private static Entity ColonySelector(Entity entity)
        {
            return entity.GetDataBlob<PositionDB>().Parent ?? entity;
        }

        public static MoveToNearestColonyAction CreateCommand(int factionId, Entity commandingEntity)
        {
            var command = MoveToNearestAction.CreateCommand<MoveToNearestColonyAction>(factionId, commandingEntity);
            command.Filter = ColonyFilter;
            command.TargetSelector = ColonySelector;
            return command;
        }

        protected override void EnsureFiltersConfigured()
        {
            Filter ??= ColonyFilter;
            TargetSelector ??= ColonySelector;
        }

        public override EntityCommand Clone()
        {
            // Must not call CreateCommand with a null entity (common after save/load).
            var command = new MoveToNearestColonyAction()
            {
                _entityCommanding = _entityCommanding,
                UseActionLanes = UseActionLanes,
                RequestingFactionGuid = RequestingFactionGuid,
                EntityCommandingGuid = EntityCommandingGuid,
                CreatedDate = CreatedDate,
                ActionOnDate = ActionOnDate,
                ActionedOnDate = ActionedOnDate,
                Filter = ColonyFilter,
                TargetSelector = ColonySelector,
                EntityFactionFilter = EntityFactionFilter,
            };
            return command;
        }
    }
}
