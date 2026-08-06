using System.Collections.Generic;
using Newtonsoft.Json;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine.Orders;

namespace Pulsar4X.Datablobs
{
    public class OrderableDB : BaseDataBlob
    {
        [JsonProperty]
        public SafeList<EntityCommand> ActionList { get; } = new SafeList<EntityCommand>();

        public List<EntityCommand> ActionsFor(string parentGoalId)
        {
            var result = new List<EntityCommand>();
            foreach (var action in ActionList)
            {
                if (action.ParentGoalId == parentGoalId)
                    result.Add(action);
            }
            return result;
        }

        public List<EntityCommand> ActionsFor(Goal goal) => ActionsFor(goal.Id);

        public void ClearFor(Goal goal) => ActionList.RemoveAll(a => a.ParentGoalId == goal.Id);

        public OrderableDB()
        {
        }

        public OrderableDB(OrderableDB db)
        {
            ActionList = new SafeList<EntityCommand>(db.ActionList);
        }

        public override object Clone()
        {
            return new OrderableDB(this);
        }
    }
}
