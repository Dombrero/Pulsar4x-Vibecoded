using Pulsar4X.Engine;
using Pulsar4X.Storage;

namespace Pulsar4X.Galaxy;

public class MassVolumeProcessor
{
    public static double CalculateDryMass(double volume, double density)
    {
        return density * volume;
    }

    /// <summary>
    /// Calculates the volume given mass and density.
    /// </summary>
    /// <param name="mass">Mass in Kg</param>
    /// <param name="density">Density in Kg/cm^3</param>
    /// <returns>Volume_km3 in Km^3</returns>
    public static double CalculateVolume_Km3_FromMassAndDesity(double mass, double density)
    {
        double volumeInCm3 = mass / density;

        // now return after converting to Km^3
        return volumeInCm3 * 1.0e-15;
    }


}