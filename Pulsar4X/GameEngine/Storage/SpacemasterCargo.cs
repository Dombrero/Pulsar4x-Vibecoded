using System;
using Pulsar4X.Interfaces;

namespace Pulsar4X.Storage
{
    /// <summary>
    /// God-mode helpers for Spacemaster / SM Mode — instant cargo and transfer edits.
    /// </summary>
    public static class SpacemasterCargo
    {
        public static long GetUnits(CargoStorageDB db, ICargoable item)
            => CargoMath.GetUnitsStored(db, item, includeEscro: false);

        /// <summary>Sets stored units exactly (add or remove as needed). Returns units after change.</summary>
        public static long SetUnits(CargoStorageDB db, ICargoable item, long desiredUnits)
        {
            desiredUnits = Math.Max(0, desiredUnits);
            long current = GetUnits(db, item);
            if (desiredUnits > current)
                CargoMath.AddCargoByUnit(db, item, desiredUnits - current);
            else if (desiredUnits < current)
                CargoMath.RemoveCargoByUnit(db, item, current - desiredUnits);
            return GetUnits(db, item);
        }

        public static void SetTransferStats(CargoStorageDB db, int rateKgPerSec, double rangeDvMps)
        {
            db.TransferRate = Math.Max(0, rateKgPerSec);
            db.TransferRangeDv_mps = Math.Max(0, rangeDvMps);
        }
    }
}
