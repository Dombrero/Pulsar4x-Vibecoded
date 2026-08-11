using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Industry;
using Pulsar4X.Names;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// One-shot migration: bring order_rework_08_08_26.sav up to the post-economy-fix start
    /// stocks (ntp, plastic, …) so existing playthroughs can test without a new game.
    /// </summary>
    [TestFixture]
    public class MigrateOrderReworkSaveEconomyTest
    {
        private static readonly string SaveFileName = "order_rework_08_08_26.sav";

        private static readonly string AppDataSavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pulsar4X", "Pulsar4X", "Saves", SaveFileName);

        private static readonly string RepoSavePath = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Saves", SaveFileName));

        // Match earth.json post-economy-fix colony cargo (byCount units).
        private static readonly (string Id, long Amount)[] TargetStocks =
        {
            ("rp-1", 50_000_000),
            ("methalox", 50_000_000),
            ("hydrolox", 50_000_000),
            ("ntp", 50_000_000),
            ("fissile-fuels", 5_000_000),
            ("plastic", 100_000),
            ("rare-earth-elements", 25_000),
            ("water", 100_000),
        };

        private static readonly string[] UnlockIds =
        {
            "electronics",
            "ordnance-construction",
            "ntp",
            "plastic",
            "fissile-fuels",
            "rare-earth-elements",
            "water",
            "methalox",
            "hydrolox",
            "rp-1",
        };

        [Test]
        public void Migrate_economy_stocks_and_unlocks()
        {
            string source = File.Exists(AppDataSavePath) ? AppDataSavePath
                : File.Exists(RepoSavePath) ? RepoSavePath
                : null!;
            Assert.That(source, Is.Not.Null.And.Not.Empty, "Save not found in AppData or repo Saves/");

            string backup = source + ".pre_economy_migrate.bak";
            File.Copy(source, backup, overwrite: true);
            TestContext.WriteLine($"Backup: {backup}");

            var game = Game.Load(File.ReadAllText(source));
            game.Settings.EnforceSingleThread = true;

            var faction = game.Factions.Values
                .Where(f => f.Id != game.GameMasterFaction.Id)
                .First(f => f.TryGetDataBlob<FactionInfoDB>(out var info) && info.KnownSystems.Count > 0);
            var factionInfo = faction.GetDataBlob<FactionInfoDB>();

            foreach (var id in UnlockIds)
            {
                try { factionInfo.Data.Unlock(id); }
                catch { /* already unlocked or unknown */ }
            }

            // Prefer Earth HQ colony (player home).
            Entity? colony = null;
            foreach (var system in game.Systems)
            {
                foreach (var c in system.GetAllEntitiesWithDataBlob<ColonyInfoDB>())
                {
                    if (c.FactionOwnerID != faction.Id) continue;
                    string name = c.GetOwnersName();
                    if (name.Contains("Earth", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("HQ", StringComparison.OrdinalIgnoreCase))
                    {
                        colony = c;
                        break;
                    }
                }
                if (colony != null) break;
            }

            colony ??= game.Systems
                .SelectMany(s => s.GetAllEntitiesWithDataBlob<ColonyInfoDB>())
                .FirstOrDefault(c => c.FactionOwnerID == faction.Id);

            Assert.That(colony, Is.Not.Null, "No player colony found");
            Assert.That(colony!.TryGetDataBlob<CargoStorageDB>(out var storage), Is.True);
            TestContext.WriteLine($"Colony: {colony.GetOwnersName()} id={colony.Id}");

            // Warehouse was clamped to 10k — ensure general + fuel stores can hold top-up.
            EnsureStoreCapacity(storage!, "general-storage", 1_000_000);
            EnsureStoreCapacity(storage!, "fuel-storage", 50_000_000);

            foreach (var (id, target) in TargetStocks)
            {
                if (!TryResolveCargoable(factionInfo, id, out var item) || item == null)
                {
                    TestContext.WriteLine($"SKIP missing cargoable: {id}");
                    continue;
                }

                if (!storage!.TypeStores.ContainsKey(item.CargoTypeID))
                    storage.TypeStores.Add(item.CargoTypeID, new TypeStore(1_000_000));

                long have = storage.GetUnitsStored(item, includeEscro: false);
                if (have >= target)
                {
                    TestContext.WriteLine($"{id}: already {have} (>= {target})");
                    continue;
                }

                long need = target - have;
                double volNeed = need * item.VolumePerUnit;
                EnsureStoreCapacity(storage, item.CargoTypeID, volNeed + 1);

                long added = storage.AddCargoByUnit(item, need);
                long after = storage.GetUnitsStored(item, includeEscro: false);
                TestContext.WriteLine($"{id}: {have} + {added} → {after} (target {target})");
                Assert.That(after, Is.GreaterThanOrEqualTo(Math.Min(target, have + added)));
            }

            string json = Game.Save(game);
            foreach (var path in new[] { AppDataSavePath, RepoSavePath }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                TestContext.WriteLine($"Wrote {path} ({new FileInfo(path).Length} bytes)");
            }
        }

        private static void EnsureStoreCapacity(CargoStorageDB storage, string typeId, double freeNeeded)
        {
            if (!storage.TypeStores.TryGetValue(typeId, out var store))
            {
                storage.TypeStores.Add(typeId, new TypeStore(Math.Max(freeNeeded, 1_000_000)));
                return;
            }

            if (store.FreeVolume >= freeNeeded)
                return;

            double add = freeNeeded - store.FreeVolume;
            store.MaxVolume += add;
            store.FreeVolume += add;
        }

        private static bool TryResolveCargoable(FactionInfoDB factionInfo, string id, out ICargoable? item)
        {
            item = null;
            var goods = factionInfo.Data.CargoGoods;
            if (goods.Contains(id))
            {
                item = goods.GetAny(id);
                return item != null;
            }

            // Unlock may have moved it; also check locked then unlock.
            if (factionInfo.Data.LockedCargoGoods.Contains(id))
            {
                factionInfo.Data.Unlock(id);
                if (goods.Contains(id))
                {
                    item = goods.GetAny(id);
                    return item != null;
                }
            }

            return false;
        }
    }
}
