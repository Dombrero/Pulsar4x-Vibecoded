using Pulsar4X.Datablobs;
using Pulsar4X.Ships;

namespace Pulsar4X.Weapons;

public class MissileLaunchersAbilityDB : BaseDataBlob
{
    public MissileLauncherAtb[] Launchers;
    public ShipDesign[] LoadedMissiles;


    public override object Clone()
    {
        throw new System.NotImplementedException();
    }
}
