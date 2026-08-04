using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Pulsar4X.Interfaces;
using Pulsar4X.Extensions;
using Pulsar4X.DataStructures;
using Pulsar4X.Factions;
using Pulsar4X.Engine;
using Pulsar4X.Storage;

namespace Pulsar4X.Industry
{
    public static class IndustryTools
    {
        /// <summary>
        /// Colony buildings for the Factory "Colony Installations" tab / Auto-install.
        /// Rule: mountable on a planet and not a ship component. Dual-use gear must use
        /// separate ship vs colony templates (e.g. battery-bank vs colony-battery-bank).
        /// </summary>
        public static bool IsColonyInstallationDesign(IConstructableDesign design)
        {
            if (design is not ComponentDesign component)
                return false;

            var mounts = component.ComponentMountType;
            return mounts.HasFlag(ComponentMountType.PlanetInstallation)
                && !mounts.HasFlag(ComponentMountType.ShipComponent);
        }

        public static void AddJob(Entity industryEntity, string plineID, IndustryJob job)
        {
            var industryDB = industryEntity.GetDataBlob<IndustryAbilityDB>();
            AddJob(industryDB, plineID, job);
        }

        public static void AddJob(IndustryAbilityDB industryDB, string plineID, IndustryJob job)
        {
            lock (industryDB.ProductionLines[plineID])
            {
                var pline = industryDB.ProductionLines[plineID];
                pline.Jobs.Add(job);
            }
        }

        public static void ChangeJobPriority(Entity industryEntity, string prodLine, string jobID, int delta)
        {
            var industryDB = industryEntity.GetDataBlob<IndustryAbilityDB>();
            var jobList = industryDB.ProductionLines[prodLine].Jobs;
            //first check that the job does still exsist in the list.
            var job = jobList.Find((obj) => obj.JobID == jobID);
            if (job != null)
            {
                var currentIndex = jobList.IndexOf(job);
                var newIndex = currentIndex + delta;
                if (newIndex <= 0)
                {
                    jobList.RemoveAt(currentIndex);
                    jobList.Insert(0, job);
                }
                else if (newIndex >= jobList.Count - 1)
                {
                    jobList.RemoveAt(currentIndex);
                    jobList.Add(job);
                }
                else
                {
                    jobList.RemoveAt(currentIndex);
                    jobList.Insert(newIndex, job);
                }
            }
        }

        public static void EditExsistingJob(Entity industryEntity, string prodLine, string jobID, bool RepeatJob = false, ushort NumberOrderd = 1, bool autoInstall = false)
        {
            var industryDB = industryEntity.GetDataBlob<IndustryAbilityDB>();
            var jobList = industryDB.ProductionLines[prodLine].Jobs;
            //first check that the job does still exsist in the list.
            var job = jobList.Find((obj) => obj.JobID == jobID);
            if (job != null)
            {
                job.Auto = RepeatJob;
                job.NumberOrdered = NumberOrderd;
                /*if (job is ConstructJob)
                {
                    var cj = (ConstructJob)job;
                    cj.InstallOn = industryEntity;
                }*/

            }
        }

        public static void CancelExsistingJob(Entity industryEntity, string prodLine, string jobID)
        {
            var industryDB = industryEntity.GetDataBlob<IndustryAbilityDB>();
            var jobList = industryDB.ProductionLines[prodLine].Jobs;
            //first check that the job does still exsist in the list.
            var job = jobList.Find((obj) => obj.JobID == jobID);
            if (job != null)
            {
                jobList.Remove(job);
            }
        }

        internal static void ConstructStuff(Entity industryEntity)
        {
            if (!industryEntity.TryGetDataBlob<CargoStorageDB>(out var stockpile))
            {
                throw new Exception("Tried to ConstructStuff on an entity with no CargoStorageDB");
            }

            if (!industryEntity.AttachedManager.Game.Factions.ContainsKey(industryEntity.FactionOwnerID))
            {
                throw new Exception("Unable to find the faction entity");
            }
            var faction = industryEntity.AttachedManager.Game.Factions[industryEntity.FactionOwnerID];

            if (!faction.TryGetDataBlob<FactionInfoDB>(out var factionInfo))
            {
                throw new Exception("Unable to find FactionInfoDB");
            }

            if (!industryEntity.TryGetDataBlob<IndustryAbilityDB>(out var industryDB))
            {
                throw new Exception("Unable to find IndustryAbilityDB");
            }

            PurgeUnconstructableElectricityJobs(industryDB, factionInfo);

            // Infrastructure and colony power efficiency both scale industry throughput.
            double infraEfficiency = InfrastructureProcessor.GetEfficiency(industryEntity);
            double powerEfficiency = Pulsar4X.Energy.ColonyPowerProcessor.GetPowerEfficiency(industryEntity);
            double efficiency = infraEfficiency * powerEfficiency;

            foreach (var (prodLineID, prodLine) in industryDB.ProductionLines.ToArray())
            {
                var industryPointsRemaining = new Dictionary<string, int>();
                foreach (var rate in prodLine.IndustryTypeRates)
                    industryPointsRemaining[rate.Key] = (int)(rate.Value * efficiency);

                foreach (var batchJob in prodLine.Jobs.ToArray())
                {
                    IConstructableDesign designInfo = factionInfo.IndustryDesigns[batchJob.ItemGuid];
                    if (!industryPointsRemaining.TryGetValue(designInfo.IndustryTypeID, out var pointsForType)
                        || pointsForType < 1)
                    {
                        batchJob.Status = IndustryJobStatus.Queued;
                        continue;
                    }

                    //total number of resources requred for a single job in this batch
                    var resourceSum = batchJob.ResourcesCosts.Sum(item => item.Value);
                    //how many construction points each resourcepoint is worth.
                    if (resourceSum == 0)
                        throw new Exception("resources can't cost 0");

                    float pointPerResource = (float)designInfo.IndustryPointCosts / (float)resourceSum;
                    bool madeProgressThisPass = false;
                    int completionsThisPass = 0;

                    // Use the remaining points dictionary in the while guard — a local copy goes
                    // stale after spending, and Math.Max(..., 1) below used to invent free IP.
                    while (
                        batchJob.NumberCompleted < batchJob.NumberOrdered &&
                        industryPointsRemaining[designInfo.IndustryTypeID] >= 1)
                    {
                        //gather availible resorces for this job.
                        //right now we take all the resources we can, for an individual item in the batch.
                        //even if we're taking more than we can use in this turn, we're using/storing it.
                        IDictionary<string, long> resourceCosts = batchJob.ResourcesRequiredRemaining;

                        var totalResourceReq = resourceCosts.Sum(item => item.Value);

                        //Note: this is editing batchjob.ResourcesRequired variable (as ref resourceCosts).
                        ConsumeResources(stockpile, ref resourceCosts);
                        //we calculate the difference between the design resources and the amount of resources we've squirreled away.

                        // this is the total of the resources that we don't have access to for this item.
                        var totalResourceStillReq = resourceCosts.Sum(item => item.Value);

                        // this is the total resources that can be used on this item.
                        var totalResourcesUsed = totalResourceReq - totalResourceStillReq;
                        // the industry Points equivelent of total used resources.
                        var totalIPEquvelent = totalResourcesUsed * pointPerResource;

                        float industryPointsToUse = Math.Min(
                            industryPointsRemaining[designInfo.IndustryTypeID],
                            batchJob.ProductionPointsLeft);
                        int pointsToUse;
                        if (totalResourceStillReq == 0)
                        {
                            // All inputs gathered — spend real remaining IP only (never invent +1).
                            pointsToUse = (int)Math.Floor(industryPointsToUse);
                        }
                        else
                        {
                            industryPointsToUse = Math.Min(industryPointsToUse, totalIPEquvelent);
                            pointsToUse = (int)Math.Floor(industryPointsToUse);
                        }

                        if (pointsToUse < 1)
                        {
                            // Partial cargo toward the job (IP equivalent floored to 0) or truly stuck.
                            if (totalResourcesUsed > 0 || madeProgressThisPass)
                                batchJob.Status = IndustryJobStatus.Processing;
                            else if (totalResourceStillReq > 0)
                                batchJob.Status = IndustryJobStatus.MissingResources;
                            else
                                batchJob.Status = IndustryJobStatus.Queued;
                            break;
                        }

                        //construct only enough for the amount of resources we have.
                        batchJob.ProductionPointsLeft -= pointsToUse;
                        industryPointsRemaining[designInfo.IndustryTypeID] -= pointsToUse;
                        madeProgressThisPass = true;
                        batchJob.Status = IndustryJobStatus.Processing;

                        if (batchJob.ProductionPointsLeft == 0 && totalResourceStillReq == 0)
                        {
                            batchJob.Status = IndustryJobStatus.Completed;
                            completionsThisPass++;
                            try
                            {
                                // Cheap Auto recipes can finish dozens of times per day — log a
                                // summary once after the pass instead of flooding the debug window.
                                if (completionsThisPass == 1)
                                {
                                    DebugTraceLog.Info("Production",
                                        $"Job complete: '{designInfo.Name}' ({designInfo.UniqueID}) on entity#{industryEntity.Id} line={prodLineID}",
                                        industryEntity.StarSysDateTime);
                                }
                                designInfo.OnConstructionComplete(industryEntity, stockpile, prodLineID, batchJob, designInfo);
                            }
                            catch (Exception ex)
                            {
                                // Keep other production lines running — a single bad completion
                                // must not abort the whole ConstructStuff pass.
                                DebugTraceLog.Error("Production",
                                    $"OnConstructionComplete failed for '{designInfo.Name}': {ex.GetType().Name}: {ex.Message}",
                                    industryEntity.StarSysDateTime);
                                break;
                            }
                            // Auto re-queues in OnConstructionComplete (NumberCompleted → 0). Keep
                            // looping so the day's remaining industry points are used.
                        }
                    }

                    if (completionsThisPass > 1)
                    {
                        DebugTraceLog.Info("Production",
                            $"Job '{designInfo.Name}' completed x{completionsThisPass} this tick on entity#{industryEntity.Id} line={prodLineID}",
                            industryEntity.StarSysDateTime);
                    }

                    if (batchJob.Auto
                        && madeProgressThisPass
                        && batchJob.NumberCompleted == 0
                        && prodLine.Jobs.Contains(batchJob))
                    {
                        batchJob.Status = IndustryJobStatus.Processing;
                    }
                }
            }
        }

        internal static void ConsumeResources(CargoStorageDB fromCargo, ref IDictionary<string, long> toUse)
        {
            foreach (var kvp in toUse.ToArray())
            {
                ICargoable? cargoItem = fromCargo.OwningEntity.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods.GetAny(kvp.Key);//fromCargo.OwningEntity.AttachedManager.Game.StaticData.GetICargoable(kvp.Key);
                if (cargoItem is null)
                {
                    if (fromCargo.OwningEntity.GetFactionOwner.GetDataBlob<FactionInfoDB>().InternalComponentDesigns.TryGetValue(kvp.Key, out var design))
                    {
                        if (design != null)
                            cargoItem = (ICargoable)design;
                    }
                    else
                    {
                        throw new Exception("Cant build from non ICargoable Items");
                    }
                }
                if (cargoItem is null)
                    throw new InvalidOperationException("Industry job references unknown cargo item.");
                string cargoTypeID = cargoItem.CargoTypeID;
                long amountUsedThisTick = 0;
                if (fromCargo.TypeStores.ContainsKey(cargoTypeID))
                {
                    if (fromCargo.TypeStores[cargoTypeID].CurrentStoreInUnits.ContainsKey(cargoItem.ID))
                    {
                        amountUsedThisTick = Math.Min(fromCargo.TypeStores[cargoTypeID].CurrentStoreInUnits[cargoItem.ID], kvp.Value);
                    }
                }

                if (amountUsedThisTick > 0)
                {
                    long used = fromCargo.RemoveCargoByUnit(cargoItem, amountUsedThisTick);
                    toUse[kvp.Key] -= used;
                }
            }
        }

        public static void AutoAddSubJobs(Entity industryEntity, IndustryJob job)
        {
            if (!industryEntity.TryGetDataBlob<CargoStorageDB>(out var stockpile))
            {
                throw new Exception("Tried to ConstructStuff on an entity with no CargoStorageDB");
            }
            if (!industryEntity.TryGetDataBlob<IndustryAbilityDB>(out var industryDB))
            {
                throw new Exception("Unable to find IndustryAbilityDB");
            }

            var resReq = job.ResourcesRequiredRemaining;
            foreach (var kvp in resReq)
            {
                ICargoable? cargoItem = industryEntity.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods.GetAny(kvp.Key);
                if (cargoItem is null)
                {
                    if (industryEntity.GetFactionOwner.GetDataBlob<FactionInfoDB>().IndustryDesigns.TryGetValue(kvp.Key, out var design)
                        && design != null)
                    {
                        cargoItem = (ICargoable)design;
                    }
                    else
                    {
                        continue;
                    }
                }
                var numStored = stockpile.GetUnitsStored(cargoItem, false);
                var numReq = kvp.Value - numStored;
                if (numReq > 0)
                {
                    if (cargoItem is IConstructableDesign)
                    {
                        IConstructableDesign des = (IConstructableDesign)cargoItem;
                        IndustryJob newjob = new IndustryJob(des);
                        newjob.InitialiseJob((ushort)numReq, false);
                        SetJobToFastest(industryDB, newjob);
                        AutoAddSubJobs(industryEntity, newjob); //recursivly add jobs.
                    }
                }
            }


        }
        internal static void SetJobToFastest(IndustryAbilityDB industrydb, IndustryJob job)
        {
            var typID = job.TypeID;
            (string lineID, int rate) bestLine = (String.Empty, 0);
            var plines = industrydb.ProductionLines;
            foreach (var line in plines)
            {
                if (!line.Value.IndustryTypeRates.TryGetValue(typID, out int rate))
                    rate = -1;
                if (rate > bestLine.rate)
                    bestLine = (line.Key, rate);
            }
            if (bestLine.lineID != String.Empty)
                AddJob(industrydb, bestLine.lineID, job);
        }

        /// <summary>
        /// Removes leftover refinery "electricity" jobs (empty ResourceCosts caused industry loops).
        /// Also strips electricity from IndustryDesigns if an old save still listed it.
        /// </summary>
        internal static void PurgeUnconstructableElectricityJobs(IndustryAbilityDB industryDB, FactionInfoDB factionInfo)
        {
            factionInfo.IndustryDesigns.Remove("electricity");

            foreach (var line in industryDB.ProductionLines.Values)
            {
                line.Jobs.RemoveAll(job =>
                    job.ItemGuid == "electricity"
                    || (factionInfo.IndustryDesigns.TryGetValue(job.ItemGuid, out var design)
                        && design is ProcessedMaterial mat
                        && string.IsNullOrEmpty(mat.IndustryTypeID)));
            }
        }
    }
}