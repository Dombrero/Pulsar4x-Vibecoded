using System;
using Pulsar4X.Colonies;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Pulsar4X.Interfaces;
using Pulsar4X.Engine;
using Pulsar4X.Events;

namespace Pulsar4X.Industry;

/// <summary>
/// Processes local construction capabilities for colonies.
/// Runs once per day to handle construction activities.
/// </summary>
public class LocalConstructionProcessor : IHotloopProcessor
{
    public TimeSpan RunFrequency
    {
        get { return TimeSpan.FromDays(1); }
    }

    public TimeSpan FirstRunOffset => TimeSpan.FromHours(6);

    public Type GetParameterType => typeof(LocalConstructionDB);

    public void Init(Game game)
    {
        // No initialization needed
    }

    /// <summary>
    /// Recomputes <see cref="LocalConstructionDB.PointsPerDay"/> from installed
    /// <see cref="LocalConstructionAtb"/> components. Driven off ComponentInstancesDB
    /// recalc (and after load) so the counter stays in sync after save/load.
    /// </summary>
    internal static void RecalcPoints(Entity colonyEntity)
    {
        if (!colonyEntity.HasDataBlob<ColonyInfoDB>()
            || !colonyEntity.TryGetDataBlob<ComponentInstancesDB>(out var instancesDB))
            return;

        long total = 0;
        foreach (var design in instancesDB.GetDesignsByType(typeof(LocalConstructionAtb)))
        {
            var atb = design.GetAttribute<LocalConstructionAtb>();
            long perInstance = (long)atb.Level * atb.PointsPerDay;
            total += perInstance * instancesDB.GetNumberOfComponentsOfDesign(design.UniqueID);
        }

        if (total <= 0)
        {
            if (colonyEntity.HasDataBlob<LocalConstructionDB>())
                colonyEntity.RemoveDataBlob<LocalConstructionDB>();
            return;
        }

        if (!colonyEntity.TryGetDataBlob<LocalConstructionDB>(out var constructionDB))
        {
            constructionDB = new LocalConstructionDB();
            colonyEntity.SetDataBlob(constructionDB);
        }

        constructionDB.PointsPerDay = total;
    }

    public void ProcessEntity(Entity entity, int deltaSeconds)
    {
        if (!entity.TryGetDataBlob<LocalConstructionDB>(out var constructionDB))
            return;

        var pointsToApply = constructionDB.PointsPerDay;

        while (pointsToApply > 0 && constructionDB.BuildQueue.Count > 0)
        {
            var currentJob = constructionDB.BuildQueue.Peek();
            var pointsNeeded = currentJob.Design.IndustryPointCosts - currentJob.PointsAccumulated;

            if (pointsToApply >= pointsNeeded)
            {
                // Complete the job
                currentJob.PointsAccumulated += pointsNeeded;
                pointsToApply -= pointsNeeded;

                // Remove completed job from queue
                constructionDB.BuildQueue.Dequeue();

                // Install the constructed component on the entity
                entity.AddComponent(currentJob.Design);

                // Publish completion event
                EventManager.Instance.Publish(
                    Event.Create(
                        EventType.ProductionCompleted,
                        entity.StarSysDateTime,
                        $"Completed construction of {currentJob.Design.Name}",
                        entity.FactionOwnerID,
                        entity.AttachedManager.ManagerID,
                        entity.Id));
            }
            else
            {
                // Partially complete the job
                currentJob.PointsAccumulated += pointsToApply;
                pointsToApply = 0;
            }
        }
    }

    public int ProcessManager(EntityManager manager, int deltaSeconds)
    {
        var entities = manager.GetAllEntitiesWithDataBlob<LocalConstructionDB>();
        foreach (var entity in entities)
        {
            ProcessEntity(entity, deltaSeconds);
        }

        return entities.Count;
    }
}
