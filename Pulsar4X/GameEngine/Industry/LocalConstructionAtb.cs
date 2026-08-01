using Pulsar4X.Components;
using Pulsar4X.Engine;
using Pulsar4X.Interfaces;

namespace Pulsar4X.Industry;

public class LocalConstructionAtb : IComponentDesignAttribute
{
    public byte Level { get; set; }
    public int PointsPerDay { get; set; }

    public LocalConstructionAtb(int level, int pointsPerDay)
    {
        Level = (byte)level;
        PointsPerDay = pointsPerDay;
    }

    // Totals are recomputed by LocalConstructionProcessor.RecalcPoints off ComponentInstancesDB
    // recalc (same pattern as InfrastructureCapacityAtb), so these are no-ops.
    public void OnComponentInstallation(Entity parentEntity, ComponentInstance componentInstance) { }

    public void OnComponentUninstallation(Entity parentEntity, ComponentInstance componentInstance) { }

    public string AtbName()
    {
        return "Local Construction";
    }

    public string AtbDescription()
    {
        return "Allows for local construction of components and ships.";
    }
}