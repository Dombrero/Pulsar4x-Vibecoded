using System;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Events;
using Pulsar4X.Factions;
using Pulsar4X.Messaging;

namespace Pulsar4X.JumpPoints;

/// <summary>
/// When a faction transits a jump gate, ensure the destination star system exists in their
/// client replica (KnownSystems + SystemRevealed) before entity transfer events fire.
/// </summary>
internal static class JumpTransitDiscovery
{
    internal static void EnsureDestinationKnown(Entity traveler, Entity destinationGate, DateTime atDateTime)
    {
        if (!traveler.IsValid || !destinationGate.IsValid)
            return;

        if (traveler.FactionOwnerID <= 0)
            return;

        var game = destinationGate.AttachedManager?.Game;
        if (game == null || !game.Factions.TryGetValue(traveler.FactionOwnerID, out var faction))
            return;

        if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo))
            return;

        string destSystemId = destinationGate.AttachedManager.ManagerID;
        if (factionInfo.KnownSystems.Contains(destSystemId))
            return;

        factionInfo.KnownSystems.Add(destSystemId);

        EventManager.Instance.Publish(
            Event.Create(
                EventType.NewSystemDiscovered,
                atDateTime,
                "New system discovered",
                traveler.FactionOwnerID,
                destSystemId,
                destinationGate.Id));

        _ = MessagePublisher.Instance.Publish(
            Message.Create(
                MessageTypes.StarSystemRevealed,
                destinationGate.Id,
                destSystemId,
                traveler.FactionOwnerID));

        if (destinationGate.TryGetDataBlob<JumpPointDB>(out var jp))
        {
            jp.IsDiscovered.Add(traveler.FactionOwnerID);
            destinationGate.AttachedManager.ShowNeutralEntityToFaction(traveler.FactionOwnerID, destinationGate.Id);
        }
    }
}
