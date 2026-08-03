namespace Pulsar4X.Energy
{
    /// <summary>
    /// Colony plant generation profile.
    /// <see cref="SolarSine"/> uses attenuated star flux (1 AU nameplate), not a day/night sine.
    /// </summary>
    public enum PowerGenProfile
    {
        Constant = 0,
        SolarSine = 1,
        WindRng = 2,
    }
}
