using Pulsar4X.Api;
using Pulsar4X.Engine;
using Pulsar4X.Messaging;
using System.Threading.Tasks;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Helpers for keeping the faction command tree (fleets + unattached ships) consistent with
    /// observers (API FleetsChanged pushes).
    /// </summary>
    public static class FleetHierarchy
    {
        /// <summary>
        /// Places a newly created ship under the faction root as an unattached ship and notifies
        /// listeners. Must be called <em>after</em> <see cref="EntityManager.AddEntity"/> — the
        /// EntityAdded message projects the fleet tree before membership is known, so without this
        /// follow-up the client never sees the ship in Fleet Management.
        /// </summary>
        public static void AttachUnattachedShip(Entity faction, Entity ship)
        {
            if (!faction.TryGetDataBlob<FleetDB>(out var fleetDB))
            {
                DebugTraceLog.Error("Production",
                    $"AttachUnattachedShip: faction#{faction.Id} has no FleetDB — ship#{ship.Id} will not appear in Fleet Management.");
                return;
            }

            if (!fleetDB.Children.Contains(ship))
                fleetDB.AddChild(ship);

            DebugTraceLog.Info("Production",
                $"AttachUnattachedShip: ship#{ship.Id} under faction#{faction.Id} (root children={fleetDB.Children.Count})",
                ship.Manager?.StarSysDateTime);

            // Observe the task — fire-and-forget Publish exceptions used to tear down the process
            // right after a successful ship spawn log line.
            _ = MessagePublisher.Instance.Publish(
                    Message.Create(MessageTypes.FleetReorganized, factionId: faction.Id))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        DebugTraceLog.Error("Production",
                            $"FleetReorganized publish failed: {t.Exception.GetBaseException().Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
