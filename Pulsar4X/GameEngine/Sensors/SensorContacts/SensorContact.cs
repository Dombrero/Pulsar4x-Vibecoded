using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pulsar4X.Engine;
using Pulsar4X.Factions;
using Pulsar4X.Messaging;
using Pulsar4X.Movement;
using Pulsar4X.Names;

namespace Pulsar4X.Sensors
{
    public enum DataFrom
    {
        Parent,
        Sensors,
        Memory
    }

    public class SensorContact
    {
        public int ActualEntityId;

        /// <summary>
        /// Must not default to <see cref="Entity.InvalidEntity"/>: that singleton is shared, and
        /// Newtonsoft <c>PreserveReferencesHandling</c> would populate it in-place during load
        /// ("A different Id has already been assigned for value Entity").
        /// </summary>
        public Entity ActualEntity = null!;

        public SensorInfoDB? SensorInfo;
        public SensorPositionDB? Position;
        //public SensorOrbitDB Orbit;

        public string Name = "UnNamed";

        [JsonConstructor]
        public SensorContact() { }

        public SensorContact(Entity factionEntity, Entity actualEntity, DateTime atDateTime)
        {
            ActualEntity = actualEntity;
            ActualEntityId = actualEntity.Id;
            SensorInfo = new SensorInfoDB(factionEntity, actualEntity, atDateTime);
            SensorInfo.SensorContact = this;
            Position = new SensorPositionDB(actualEntity.GetDataBlob<PositionDB>());
            var factionInfoDB = factionEntity.GetDataBlob<FactionInfoDB>();
            if (!factionInfoDB.SensorContacts.ContainsKey(actualEntity.Id))
                factionInfoDB.SensorContacts.Add(actualEntity.Id, this);
            Name = actualEntity.GetDataBlob<NameDB>().GetName(factionEntity);

            MessagePublisher.Instance.Subscribe(MessageTypes.EntityRemoved, EntityRemoved, msg => msg.EntityId != null && msg.EntityId.Value == actualEntity.Id);
        }

        async Task EntityRemoved(Message message)
        {
            await Task.Run(() =>
            {
                if (Position is not null)
                    Position.GetDataFrom = DataFrom.Memory;
            });
        }

    }
}
