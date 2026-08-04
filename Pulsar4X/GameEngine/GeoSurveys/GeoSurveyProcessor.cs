using System;
using Pulsar4X.Engine;
using Pulsar4X.Events;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Industry;
using Pulsar4X.Interfaces;
using Pulsar4X.Messaging;

namespace Pulsar4X.GeoSurveys;

public class GeoSurveyProcessor : IInstanceProcessor
{
    public Entity Fleet { get; internal set; } = Entity.InvalidEntity;
    public Entity Target { get; internal set; } = Entity.InvalidEntity;
    public GeoSurveyProcessor() { }

    public GeoSurveyProcessor(Entity fleet, Entity target)
    {
        Fleet = fleet;
        Target = target;
    }

    internal override void ProcessEntity(Entity entity, DateTime atDateTime)
    {
        uint totalSurveyPoints = GetSurveyPointsAtTarget(Fleet, Target);

        if (totalSurveyPoints == 0)
            return; // Nobody on station yet — do not progress remotely.

        if (Target.TryGetDataBlob<GeoSurveyableDB>(out var geoSurveyableDB))
        {
            if (!geoSurveyableDB.GeoSurveyStatus.ContainsKey(Fleet.FactionOwnerID))
                geoSurveyableDB.GeoSurveyStatus[Fleet.FactionOwnerID] = geoSurveyableDB.PointsRequired;

            if (totalSurveyPoints >= geoSurveyableDB.GeoSurveyStatus[Fleet.FactionOwnerID])
            {
                // Survey is complete
                geoSurveyableDB.GeoSurveyStatus[Fleet.FactionOwnerID] = 0;

                // Grant partial access to mineral data
                if (Target.TryGetDataBlob<MineralsDB>(out var mineralsDB))
                {
                    var factionMask = Fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().FactionMask;
                    mineralsDB.GrantFactionPartialAccess(factionMask);
                }

                EventManager.Instance.Publish(
                    Event.Create(
                        EventType.GeoSurveyCompleted,
                        atDateTime,
                        $"Geo Survey of {Target.GetName(Fleet.FactionOwnerID)} complete",
                        Fleet.FactionOwnerID,
                        Target.AttachedManager.ManagerID,
                        Target.Id));

                PublishTargetChanged();
            }
            else
            {
                geoSurveyableDB.GeoSurveyStatus[Fleet.FactionOwnerID] -= totalSurveyPoints;
                PublishTargetChanged();
            }
        }
    }

    private void PublishTargetChanged()
    {
        _ = MessagePublisher.Instance.Publish(Message.Create(
            MessageTypes.EntityChanged,
            entityId: Target.Id,
            systemId: Target.AttachedManager.ManagerID,
            factionId: Fleet.FactionOwnerID));
    }

    private uint GetSurveyPointsAtTarget(Entity fleet, Entity target)
    {
        uint totalSurveyPoints = 0;

        if (fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
        {
            foreach (var child in fleetDB.Children)
            {
                if (child.HasDataBlob<FleetDB>())
                {
                    totalSurveyPoints += GetSurveyPointsAtTarget(child, target);
                    continue;
                }

                if (!FleetOrderCleanup.IsShipAtBody(child, target))
                    continue;

                totalSurveyPoints += GetLocalSurveyPoints(child);
            }
        }
        else
        {
            if (FleetOrderCleanup.IsShipAtBody(fleet, target))
                totalSurveyPoints += GetLocalSurveyPoints(fleet);
        }

        return totalSurveyPoints;
    }

    private static uint GetLocalSurveyPoints(Entity entity)
    {
        if (entity.TryGetDataBlob<GeoSurveyAbilityDB>(out var geoSurveyAbilityDB))
            return geoSurveyAbilityDB.Speed;
        return 0;
    }
}