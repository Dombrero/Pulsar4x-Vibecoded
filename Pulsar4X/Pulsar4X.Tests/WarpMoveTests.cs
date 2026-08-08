using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pulsar4X.Engine;
using Pulsar4X.Movement;

namespace Pulsar4X.Tests
{
    public class WarpMoveTests
    {
        [Test]
        public void ProcessForType_SkipsWarpBlobWithInvalidOwner()
        {
            // After EndWarpMove RemoveDataBlob, OwningEntity becomes InvalidEntity (never null).
            // ProcessSystem used to die with: Entity#-1 has no Manager (SetDataBlob).
            var orphan = new WarpMovingDB();
            Assert.That(orphan.OwningEntity, Is.EqualTo(Entity.InvalidEntity));
            Assert.DoesNotThrow(() => MoveStateProcessor.ProcessForType(orphan, DateTime.UtcNow));
            Assert.DoesNotThrow(() => MoveStateProcessor.ProcessForType(
                new List<WarpMovingDB> { orphan }, DateTime.UtcNow));
        }
    }
}
