using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Engine;
using Pulsar4X.Events;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Interfaces;
using Pulsar4X.Messaging;
using Pulsar4X.Movement;

namespace Pulsar4X.JumpPoints;

public class JPSurveyProcessor : IHotloopProcessor
{
    // Match GeoSurvey / component template ("survey points per day").
    public TimeSpan RunFrequency { get; } = TimeSpan.FromDays(1);
    public TimeSpan FirstRunOffset { get; } = TimeSpan.FromHours(1);
    public Type GetParameterType { get; } = typeof(JPSurveyDB);

    public JPSurveyProcessor() { }

    public void Init(Game game)
    {
    }

    public void ProcessEntity(Entity entity, int deltaSeconds)
    {
        if (entity.TryGetDataBlob<JPSurveyDB>(out var jpSurveyDB)
            && entity.TryGetDataBlob<JPSurveyAbilityDB>(out var jpSurveyAbilityDB)
            && entity.AttachedManager.TryGetDataBlob<JPSurveyableDB>(jpSurveyDB.TargetId, out JPSurveyableDB? jpSurveyableDB)
            && jpSurveyableDB is not null)
        {
            // Factions are lazily added to the surveys
            if (!jpSurveyableDB.SurveyPointsRemaining.ContainsKey(entity.FactionOwnerID))
                jpSurveyableDB.SurveyPointsRemaining[entity.FactionOwnerID] = jpSurveyableDB.PointsRequired;

            // Check if the survey has been completed (possibly some other entity completed the survey already
            if (jpSurveyableDB.SurveyPointsRemaining[entity.FactionOwnerID] == 0)
            {
                // If the survey is completed remove the JPSurveyDB and return
                entity.RemoveDataBlob<JPSurveyDB>();
                try { FleetOrderProcessor.TryEvaluateNow(entity); }
                catch { /* standing wake is best-effort */ }
                return;
            }

            // Make sure the surveyor is within distance of the target
            var distance = MoveMath.GetDistanceBetween(entity, jpSurveyableDB.OwningEntity);
            if (distance < 100000) // FIXME: needs to be an attribute of the JPSurveyAbilityDB
            {
                if (jpSurveyAbilityDB.Speed >= jpSurveyableDB.SurveyPointsRemaining[entity.FactionOwnerID])
                {
                    RollToDiscoverJumpPoint(entity.StarSysDateTime, entity, jpSurveyableDB.OwningEntity);
                    MarkSurveyAsComplete(jpSurveyableDB, entity, entity.StarSysDateTime);
                    try { FleetOrderProcessor.TryEvaluateNow(entity); }
                    catch { /* standing wake is best-effort */ }
                }
                else
                {
                    jpSurveyableDB.SurveyPointsRemaining[entity.FactionOwnerID] -= jpSurveyAbilityDB.Speed;
                }
            }
        }
    }

    public int ProcessManager(EntityManager manager, int deltaSeconds)
    {
        List<JPSurveyDB> surveyors = manager.GetAllDataBlobsOfType<JPSurveyDB>();

        foreach (var db in surveyors)
        {
            ProcessEntity(db.OwningEntity, deltaSeconds);
        }

        return surveyors.Count;
    }

    private void MarkSurveyAsComplete(JPSurveyableDB jpSurveyableDB, Entity surveyingEntity, DateTime atDateTime)
    {
        // Mark the survey as complete
        jpSurveyableDB.SurveyPointsRemaining[surveyingEntity.FactionOwnerID] = 0;

        // Hide the survey location from the faction that just completed the survey
        jpSurveyableDB.OwningEntity.AttachedManager.HideNeutralEntityFromFaction(surveyingEntity.FactionOwnerID, jpSurveyableDB.OwningEntity.Id);

        EventManager.Instance.Publish(
            Event.Create(
                EventType.JumpPointSurveyCompleted,
                atDateTime,
                $"Survey of {jpSurveyableDB.OwningEntity.GetName(surveyingEntity.FactionOwnerID)} complete",
                surveyingEntity.FactionOwnerID,
                jpSurveyableDB.OwningEntity.AttachedManager.ManagerID,
                jpSurveyableDB.OwningEntity.Id));
    }

    private void RollToDiscoverJumpPoint(DateTime atDateTime, Entity discoveringEntity, Entity anomaly)
    {
        // Chance = undiscovered JPs / remaining unsurveyed anomalies (including this one).
        // On success the JP is moved to this anomaly — the anomaly *is* the grav signature.
        var surveyLocationsRemaining = discoveringEntity.AttachedManager.GetAllDataBlobsOfType<JPSurveyableDB>()
                                                        .Where(db => !db.IsSurveyComplete(discoveringEntity.FactionOwnerID))
                                                        .ToList();
        var jpRemaining = discoveringEntity.AttachedManager.GetAllDataBlobsOfType<JumpPointDB>()
                                           .Where(db => !db.IsDiscovered.Contains(discoveringEntity.FactionOwnerID))
                                           .ToList();

        if (surveyLocationsRemaining.Count == 0)
        {
            DebugTraceLog.Warn("Standing",
                $"JP survey roll skipped — no remaining anomaly sites (jpLeft={jpRemaining.Count})",
                atDateTime);
            return;
        }

        if (jpRemaining.Count == 0)
        {
            DebugTraceLog.Info("Standing",
                $"JP survey of anomaly complete — no undiscovered jump points left in system " +
                $"(sitesLeft={surveyLocationsRemaining.Count})",
                atDateTime);
            return;
        }

        var chance = (double)jpRemaining.Count / (double)surveyLocationsRemaining.Count;
        var roll = anomaly.AttachedManager.RNGNextDouble();

        if (chance >= roll)
        {
            var jp = jpRemaining[anomaly.AttachedManager.RNGNext(0, jpRemaining.Count)];
            PlaceJumpPointAtAnomaly(jp.OwningEntity, anomaly, discoveringEntity.FactionOwnerID);
            jp.IsDiscovered.Add(discoveringEntity.FactionOwnerID);

            // Show the jump point to the faction that just completed the survey
            jp.OwningEntity.AttachedManager.ShowNeutralEntityToFaction(discoveringEntity.FactionOwnerID, jp.OwningEntity.Id);

            DebugTraceLog.Info("Standing",
                $"Jump Point discovered at anomaly (chance={chance:0.##}, roll={roll:0.##}, " +
                $"jpLeft={jpRemaining.Count}, sitesLeft={surveyLocationsRemaining.Count})",
                atDateTime);

            EventManager.Instance.Publish(
                Event.Create(
                    EventType.JumpPointDetected,
                    atDateTime,
                    $"Jump Point discovered",
                    discoveringEntity.FactionOwnerID,
                    jp.OwningEntity.AttachedManager.ManagerID,
                    jp.OwningEntity.Id));

            _ = MessagePublisher.Instance.Publish(Message.Create(
                MessageTypes.EntityChanged,
                entityId: jp.OwningEntity.Id,
                systemId: jp.OwningEntity.AttachedManager.ManagerID,
                factionId: discoveringEntity.FactionOwnerID));

            // If this was the last jump point, hide the rest of the survey locations
            if (jpRemaining.Count == 1)
            {
                foreach (var surveyLocation in surveyLocationsRemaining)
                {
                    if (surveyLocation.OwningEntity.Id == anomaly.Id) continue;

                    surveyLocation.OwningEntity.AttachedManager.HideNeutralEntityFromFaction(
                        discoveringEntity.FactionOwnerID, surveyLocation.OwningEntity.Id);
                }
            }

            RevealOtherSide(jp, atDateTime, discoveringEntity);
        }
        else
        {
            DebugTraceLog.Info("Standing",
                $"JP survey found nothing (chance={chance:0.##}, roll={roll:0.##}, " +
                $"jpLeft={jpRemaining.Count}, sitesLeft={surveyLocationsRemaining.Count})",
                atDateTime);
        }
    }

    /// <summary>
    /// The surveyed anomaly is the gravitational locus — relocate the (previously hidden)
    /// jump-point entity onto the anomaly so discovery happens where the ship surveyed.
    /// </summary>
    private static void PlaceJumpPointAtAnomaly(Entity jumpPoint, Entity anomaly, int discoveringFactionId)
    {
        if (!jumpPoint.TryGetDataBlob<PositionDB>(out var jpPos)
            || !anomaly.TryGetDataBlob<PositionDB>(out var anomalyPos))
            return;

        jpPos.AbsolutePosition = anomalyPos.AbsolutePosition;
        jpPos.MoveType = PositionDB.MoveTypes.None;
        jpPos.Velocity = default;

        if (jumpPoint.TryGetDataBlob<Names.NameDB>(out var jpName)
            && anomaly.TryGetDataBlob<Names.NameDB>(out var anomalyName))
        {
            string anomalyLabel = anomalyName.OwnersName;
            if (!string.IsNullOrEmpty(anomalyLabel) && anomalyLabel.Contains('#'))
                jpName.SetName(discoveringFactionId, $"Jump Point ({anomalyLabel})");
        }
    }

    private void RevealOtherSide(JumpPointDB jumpPointDB, DateTime atDateTime, Entity discoveringEntity)
    {
        // Skip if no destination is linked (DestinationId defaults to 0 which could match an unrelated entity)
        if (jumpPointDB.DestinationId <= 0)
            return;

        if (discoveringEntity.AttachedManager.TryGetGlobalEntityById(jumpPointDB.DestinationId, out var destinationEntity)
            && destinationEntity.HasDataBlob<JumpPointDB>())
        {
            var factionInfoDB = discoveringEntity.AttachedManager.Game.Factions[discoveringEntity.FactionOwnerID].GetDataBlob<FactionInfoDB>();

            // Check to see if the system has been discovered yet
            if (!factionInfoDB.KnownSystems.Contains(destinationEntity.AttachedManager.ManagerID))
            {
                factionInfoDB.KnownSystems.Add(destinationEntity.AttachedManager.ManagerID);

                EventManager.Instance.Publish(
                    Event.Create(
                        EventType.NewSystemDiscovered,
                        atDateTime,
                        $"New system discovered",
                        discoveringEntity.FactionOwnerID,
                        destinationEntity.AttachedManager.ManagerID,
                        destinationEntity.Id));

                _ = MessagePublisher.Instance.Publish(
                    Message.Create(
                        MessageTypes.StarSystemRevealed,
                        destinationEntity.Id,
                        destinationEntity.AttachedManager.ManagerID,
                        discoveringEntity.FactionOwnerID));
            }

            // Reveal the JP
            if (destinationEntity.TryGetDataBlob<JumpPointDB>(out var destinationDB))
            {
                destinationDB.IsDiscovered.Add(discoveringEntity.FactionOwnerID);
                destinationEntity.AttachedManager.ShowNeutralEntityToFaction(discoveringEntity.FactionOwnerID, destinationEntity.Id);

                EventManager.Instance.Publish(
                    Event.Create(
                        EventType.JumpPointDetected,
                        atDateTime,
                        $"Jump Point discovered",
                        discoveringEntity.FactionOwnerID,
                        destinationEntity.AttachedManager.ManagerID,
                        destinationEntity.Id));
            }

        }
    }

}