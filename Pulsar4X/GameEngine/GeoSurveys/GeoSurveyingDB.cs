using Pulsar4X.Datablobs;

namespace Pulsar4X.GeoSurveys;

/// <summary>
/// Marks a ship that is actively contributing geo-survey points at <see cref="TargetId"/>.
/// Set/cleared by <see cref="GeoSurveyOrder"/> — same idea as <c>JPSurveyDB</c> for grav surveys.
/// </summary>
public class GeoSurveyingDB : BaseDataBlob
{
    public int TargetId { get; set; }
}
