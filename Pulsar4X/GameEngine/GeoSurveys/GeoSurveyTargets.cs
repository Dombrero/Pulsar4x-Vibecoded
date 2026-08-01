using Pulsar4X.Colonies;
using Pulsar4X.Engine;

namespace Pulsar4X.GeoSurveys;

/// <summary>
/// Shared eligibility for geo-survey standing/issue orders.
/// Colonised bodies (homeworld etc.) are excluded — they are not survey destinations.
/// </summary>
public static class GeoSurveyTargets
{
    public static bool IsEligible(Entity body, int factionId)
    {
        if (!body.TryGetDataBlob<GeoSurveyableDB>(out var geo))
            return false;
        if (geo.IsSurveyComplete(factionId))
            return false;
        if (HasOwnedColony(body, factionId))
            return false;
        return true;
    }

    public static bool HasOwnedColony(Entity body, int factionId)
    {
        var manager = body.Manager;
        if (manager == null)
            return false;

        foreach (var colony in manager.GetAllEntitiesWithDataBlob<ColonyInfoDB>())
        {
            if (colony.FactionOwnerID != factionId)
                continue;
            if (colony.TryGetDataBlob<ColonyInfoDB>(out var info) && info.PlanetEntity == body)
                return true;
        }

        return false;
    }

    public static int CountEligible(EntityManager manager, int factionId)
    {
        int count = 0;
        foreach (var body in manager.GetAllEntitiesWithDataBlob<GeoSurveyableDB>())
        {
            if (IsEligible(body, factionId))
                count++;
        }
        return count;
    }
}
