using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests;

[TestFixture]
public class JumpOrderFleetTests
{
    [Test]
    public void JumpOrder_removed_from_fleet_queue_when_ship_transits()
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
        var srcDb = srcJp.GetDataBlob<JumpPointDB>();
        srcDb.DestinationId = dstJp.Id;
        dstJp.GetDataBlob<JumpPointDB>().DestinationId = srcJp.Id;
        srcDb.IsDiscovered.Add(game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id).Id);

        var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);

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
            new NameDB("Science Fleet"),
        });
        fleetDB.FlagShipID = ship.Id;
        fleetDB.AddChild(ship);

        Assert.IsTrue(JumpOrder.CreateAndExecute(game, faction, fleet, srcDb));

        var fleetOrders = fleet.GetDataBlob<OrderableDB>();
        Assert.IsFalse(
            fleetOrders.ActionList.Any(a => a is JumpOrder),
            "Jump fleet order should leave the queue once transit completes");

        Assert.AreSame(dest, ship.Manager);
        Assert.AreSame(dest, fleet.Manager);
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
