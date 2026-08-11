using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests;

[TestFixture]
public class CrossSystemRefuelTests
{
    [Test]
    public void RefuelAction_in_remote_system_enqueues_jump_toward_home_colony()
    {
        var start = new DateTime(2100, 1, 1);
        var game = TestingUtilities.CreateTestUniverse(2, start, false);
        game.Settings.EnforceSingleThread = true;

        var systems = game.Systems.Distinct().ToArray();
        var home = systems[0];
        var remote = systems[1];
        home.SetActivityState(SystemActivityState.Foreground);
        remote.SetActivityState(SystemActivityState.Foreground);

        var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
        var factionInfo = faction.GetDataBlob<FactionInfoDB>();

        Entity srcJp = remote.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
            ?? CreateJp(remote, "JP-Remote");
        Entity dstJp = home.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
            ?? CreateJp(home, "JP-Home");
        srcJp.GetDataBlob<JumpPointDB>().DestinationId = dstJp.Id;
        dstJp.GetDataBlob<JumpPointDB>().DestinationId = srcJp.Id;
        srcJp.GetDataBlob<JumpPointDB>().IsDiscovered.Add(faction.Id);
        dstJp.GetDataBlob<JumpPointDB>().IsDiscovered.Add(faction.Id);

        var fuel = UnlockAnyFuel(factionInfo);
        var homeStar = home.GetFirstEntityWithDataBlob<StarInfoDB>();
        var colonyStorage = new CargoStorageDB(fuel.CargoTypeID, 1e12)
        {
            TransferRate = 5000,
            TransferRangeDv_mps = 1e12,
        };
        var colony = Entity.Create(faction.Id);
        home.AddEntity(colony, new List<BaseDataBlob>
        {
            new ColonyInfoDB(new Dictionary<int, long>(), homeStar),
            colonyStorage,
            new PositionDB(0, 0, 0, homeStar),
            new MassVolumeDB { MassDry = 1e9 },
            new NameDB("Home HQ", faction.Id, "Home HQ"),
            new OrderableDB(),
        });
        colonyStorage.AddCargoByUnit(fuel, 50_000_000);
        factionInfo.Colonies.Add(colony);

        var shipStorage = new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
        {
            TransferRate = 5000,
            TransferRangeDv_mps = 1e12,
        };
        // Nearly empty — standing Refuel would fire.
        shipStorage.AddCargoByUnit(fuel, 1);

        var ship = Entity.Create(faction.Id);
        remote.AddEntity(ship, new List<BaseDataBlob>
        {
            shipStorage,
            new ShipInfoDB(),
            new NameDB("Scout"),
            new PositionDB(0, 0, 0, srcJp),
            new OrderableDB(),
            new WarpAbilityDB
            {
                MaxSpeed = 1e9,
                EnergyType = fuel.UniqueID,
                BubbleCreationCost = 1,
                BubbleSustainCost = 0,
            },
        });

        var fleet = Entity.Create(faction.Id);
        var fleetDB = new FleetDB();
        remote.AddEntity(fleet, new List<BaseDataBlob>
        {
            fleetDB,
            new OrderableDB(),
            new NameDB("Survey Fleet"),
            new PositionDB(0, 0, 0, srcJp),
        });
        fleetDB.FlagShipID = ship.Id;
        fleetDB.AddChild(ship);
        // Simulate never having refueled (old save) — seed must come from Colonies.
        fleetDB.LastRefuelSystemId = null;

        var refuel = RefuelAction.CreateCommand(faction.Id, fleet);
        fleet.GetDataBlob<OrderableDB>().ActionList.Add(refuel);
        refuel.BindCommandingEntity(fleet);
        refuel.Execute(remote.StarSysDateTime);

        var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
        Assert.That(orders.Any(a => a is JumpOrder), Is.True,
            "Refuel in a foreign system must enqueue a Jump toward the home colony");
        Assert.That(orders.Any(a => a is RefuelAction && !ReferenceEquals(a, refuel)), Is.True,
            "A follow-up Refuel must be queued after the jump");
        Assert.That(refuel.IsFinished(), Is.True);
        Assert.That(fleetDB.StandingSuppressUntil, Is.Null,
            "Successful jump planning must not suppress standing");

        var jump = orders.OfType<JumpOrder>().First();
        Assert.That(jump.EntityCommanding.IsValid, Is.True,
            "JumpOrder follow-up must be bound to the fleet (otherwise Execute no-ops forever)");
        Assert.That(jump.JumpGate, Is.Not.Null);

        // Drive OrderableProcessor so JumpOrder.Execute dispatches ship warps.
        var processor = new OrderableProcessor();
        processor.ProcessEntity(fleet, remote.StarSysDateTime);
        Assert.That(jump.IsRunning, Is.True, "Bound JumpOrder must start and dispatch ship transit");

        bool shipHasTransitOrders = ship.TryGetDataBlob<OrderableDB>(out var shipOrders)
            && shipOrders.ActionList.Any(a => a is WarpMoveCommand or ShipJumpCommand);
        bool shipAlreadyTransited = ship.AttachedManager == home;
        Assert.That(shipHasTransitOrders || shipAlreadyTransited, Is.True,
            "Ships must receive warp-to-gate / ShipJump, or already transit when already at the gate");
    }

    [Test]
    public void JumpTransit_registers_gates_in_InternalKnownJumpPoints()
    {
        var start = new DateTime(2100, 1, 1);
        var game = TestingUtilities.CreateTestUniverse(2, start, false);
        game.Settings.EnforceSingleThread = true;

        var systems = game.Systems.Distinct().ToArray();
        var source = systems[0];
        var dest = systems[1];
        source.SetActivityState(SystemActivityState.Foreground);
        dest.SetActivityState(SystemActivityState.Stasis);

        Entity srcJp = source.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
            ?? CreateJp(source, "JP-Src");
        Entity dstJp = dest.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
            ?? CreateJp(dest, "JP-Dst");
        srcJp.GetDataBlob<JumpPointDB>().DestinationId = dstJp.Id;
        dstJp.GetDataBlob<JumpPointDB>().DestinationId = srcJp.Id;

        var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
        var factionInfo = faction.GetDataBlob<FactionInfoDB>();
        factionInfo.InternalKnownJumpPoints.Clear();

        var ship = Entity.Create(faction.Id);
        source.AddEntity(ship, new List<BaseDataBlob>
        {
            new ShipInfoDB(),
            new NameDB("Scout"),
            new PositionDB(0, 0, 0, srcJp),
            new OrderableDB(),
            new WarpAbilityDB(),
        });

        var fleet = Entity.Create(faction.Id);
        var fleetDB = new FleetDB();
        source.AddEntity(fleet, new List<BaseDataBlob>
        {
            fleetDB,
            new OrderableDB(),
            new NameDB("Fleet"),
        });
        fleetDB.FlagShipID = ship.Id;
        fleetDB.AddChild(ship);

        Assert.IsTrue(JumpOrder.CreateAndExecute(game, faction, fleet, srcJp.GetDataBlob<JumpPointDB>()));

        Assert.That(factionInfo.InternalKnownJumpPoints.ContainsKey(source.ID), Is.True);
        Assert.That(factionInfo.InternalKnownJumpPoints.ContainsKey(dest.ID), Is.True);
        Assert.That(
            factionInfo.InternalKnownJumpPoints[source.ID].Any(e => e.Id == srcJp.Id),
            Is.True);
        Assert.That(
            factionInfo.InternalKnownJumpPoints[dest.ID].Any(e => e.Id == dstJp.Id),
            Is.True);
    }

    private static ICargoable UnlockAnyFuel(FactionInfoDB factionInfo)
    {
        var data = factionInfo.Data;
        foreach (var id in new[] { "hydrolox", "rp-1", "methalox" })
        {
            if (data.CargoGoods.Contains(id))
                return data.CargoGoods.GetAny(id)!;
            if (data.LockedCargoGoods.Contains(id))
            {
                data.Unlock(id);
                return data.CargoGoods.GetAny(id)!;
            }
        }

        var material = data.LockedCargoGoods.GetMaterialsList().First();
        data.Unlock(material.UniqueID);
        return data.CargoGoods.GetAny(material.UniqueID)!;
    }

    private static Entity CreateJp(StarSystem system, string name)
    {
        var jp = Entity.Create();
        jp.FactionOwnerID = Game.NeutralFactionId;
        system.AddEntity(jp, new List<BaseDataBlob>
        {
            new NameDB(name),
            new PositionDB(Distance.AuToMt(2), 0, 0),
            new JumpPointDB(),
        });
        return jp;
    }
}
