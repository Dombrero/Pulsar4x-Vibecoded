using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Api;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests.API;

[TestFixture]
public class ApiJumpClientReplicationTests : ApiTestBase
{
    [Test]
    public void ShipJump_replicates_into_client_galaxy_when_destination_was_unknown()
    {
        _game = TestingUtilities.CreateTestUniverse(2, new DateTime(2100, 1, 1));
        _game.Settings.EnforceSingleThread = true;
        (_server as IDisposable)?.Dispose();
        _server = new EngineGameServer(_game);
        _projector = new GameProjector(_game);

        var systems = _game.Systems.Distinct().ToArray();
        var source = systems[0];
        var dest = systems[1];

        Entity srcJp = source.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
            ?? CreateJp(source, "JP-Src");
        Entity dstJp = dest.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
            ?? CreateJp(dest, "JP-Dst");
        var srcDb = srcJp.GetDataBlob<JumpPointDB>();
        srcDb.DestinationId = dstJp.Id;
        dstJp.GetDataBlob<JumpPointDB>().DestinationId = srcJp.Id;

        var faction = _game.Factions.Values.First(f => f.Id != _game.GameMasterFaction.Id);
        srcDb.IsDiscovered.Add(faction.Id);

        var ship = Entity.Create(faction.Id);
        source.AddEntity(ship, new List<BaseDataBlob>
        {
            new ShipInfoDB(),
            new NameDB("Scout"),
            new PositionDB(0, 0, 0, srcJp),
            new OrderableDB(),
        });

        var fleet = Entity.Create(faction.Id);
        var fleetDB = new FleetDB();
        source.AddEntity(fleet, new List<BaseDataBlob>
        {
            fleetDB,
            new NameDB("Science Fleet"),
        });
        faction.GetDataBlob<FleetDB>().RootDB!.AddChild(fleet);
        fleetDB.FlagShipID = ship.Id;
        fleetDB.AddChild(ship);

        var client = ClientFactory.CreateLocalClient(_server);
        var connect = client.ConnectAsync(new ConnectRequest { PlayerName = "Tester" }).GetAwaiter().GetResult();
        Assert.That(connect.Success, Is.True);
        client.Update();

        Assert.That(client.Galaxy.GetSystem(dest.ID), Is.Null,
            "destination must not be replicated before transit reveals it");

        Assert.IsTrue(_game.OrderHandler.HandleOrder(ShipJumpCommand.Create(ship, srcDb)));
        dest.Transfer(fleet);
        FleetFlagshipSync.TryResolveFlagship(fleet, fleetDB, out _);

        for (var i = 0; i < 64; i++)
            client.Update();

        var destReplica = client.Galaxy.GetSystem(dest.ID);
        Assert.That(destReplica, Is.Not.Null, "destination system should exist on client after jump");
        Assert.That(destReplica!.GetEntity(ship.Id), Is.Not.Null,
            "transited ship should be visible in destination system replica");
        Assert.That(client.Galaxy.GetSystem(source.ID)?.GetEntity(ship.Id), Is.Null,
            "ship should no longer appear in source system replica");

        Assert.That(client.Galaxy.Fleets.Any(f => f.Id == fleet.Id && f.Ships.Any(s => s.Id == ship.Id)), Is.True,
            "Science Fleet should remain in the sidebar fleet tree after jump");
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
