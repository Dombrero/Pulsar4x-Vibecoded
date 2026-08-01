namespace Pulsar4X.JumpPoints;

public static class JPSurveyableDBExtensions
{
    public static bool IsSurveyComplete(this JPSurveyableDB geoSurveyableDB, int factionId)
    {
        // Never surveyed → not complete. Null dict must not throw (standing FindNearest looped on NRE).
        if (geoSurveyableDB?.SurveyPointsRemaining == null)
            return false;

        return geoSurveyableDB.SurveyPointsRemaining.ContainsKey(factionId)
            && geoSurveyableDB.SurveyPointsRemaining[factionId] == 0;
    }

    public static bool HasSurveyStarted(this JPSurveyableDB geoSurveyableDB, int factionId)
    {
        if (geoSurveyableDB?.SurveyPointsRemaining == null)
            return false;

        return geoSurveyableDB.SurveyPointsRemaining.ContainsKey(factionId);
    }
}
