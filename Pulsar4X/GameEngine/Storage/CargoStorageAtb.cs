using Pulsar4X.Engine;
using Pulsar4X.Interfaces;
using Pulsar4X.Datablobs;
using Pulsar4X.Components;

namespace Pulsar4X.Storage;
public class CargoStorageAtb : IComponentDesignAttribute
{
    public string? StoreTypeID;
    public double MaxVolume;

    public CargoStorageAtb(string storeTypeID, double maxVolume)
    {
        StoreTypeID = storeTypeID;
        MaxVolume = maxVolume;
    }

    public void OnComponentInstallation(Entity parentEntity, ComponentInstance componentInstance)
    {
        if (!parentEntity.HasDataBlob<CargoStorageDB>())
        {
            var newdb = new CargoStorageDB(StoreTypeID, MaxVolume);
            parentEntity.SetDataBlob(newdb);
        }
        else
        {
            var db = parentEntity.GetDataBlob<CargoStorageDB>();
            if (db.TypeStores.ContainsKey(StoreTypeID))
            {
                db.TypeStores[StoreTypeID].MaxVolume += MaxVolume;
                db.TypeStores[StoreTypeID].FreeVolume += MaxVolume;
            }
            else
            {
                db.TypeStores.Add(StoreTypeID, new TypeStore(MaxVolume));
            }
        }
    }

    public void OnComponentUninstallation(Entity parentEntity, ComponentInstance componentInstance)
    {

    }

    public string AtbName()
    {
        return "Cargo Volume";
    }

    public string AtbDescription()
    {
        return "Adds " + MaxVolume + " m^3 Volume to parent cargo storage";
    }
}
