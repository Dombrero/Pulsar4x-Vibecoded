using System.Collections.Generic;
using Pulsar4X.Engine;
using Pulsar4X.Factions;

namespace Pulsar4X.JumpPoints;

/// <summary>
/// Keeps <see cref="FactionInfoDB.InternalKnownJumpPoints"/> in sync with surveyed/transited gates.
/// Without this, cross-system refuel pathfinding always fails even after a successful jump.
/// </summary>
internal static class JumpPointKnowledge
{
    internal static void Register(Entity faction, Entity jumpPointEntity)
    {
        if (!faction.IsValid || !jumpPointEntity.IsValid)
            return;
        if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo))
            return;
        if (!jumpPointEntity.HasDataBlob<JumpPointDB>())
            return;

        string systemId = jumpPointEntity.AttachedManager?.ManagerID ?? string.Empty;
        if (string.IsNullOrEmpty(systemId))
            return;

        if (!factionInfo.InternalKnownJumpPoints.TryGetValue(systemId, out var list))
        {
            list = new List<Entity>();
            factionInfo.InternalKnownJumpPoints[systemId] = list;
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].IsValid && list[i].Id == jumpPointEntity.Id)
                return;
        }

        list.Add(jumpPointEntity);
    }

    internal static void RegisterForFactionId(Game game, int factionId, Entity jumpPointEntity)
    {
        if (game == null || factionId <= 0)
            return;
        if (!game.Factions.TryGetValue(factionId, out var faction))
            return;
        Register(faction, jumpPointEntity);
    }
}
