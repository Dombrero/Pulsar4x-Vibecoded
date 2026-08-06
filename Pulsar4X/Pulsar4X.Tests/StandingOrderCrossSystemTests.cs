using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests;

[TestFixture]
public class StandingOrderCrossSystemTests
{
    [Test]
    public void Flagship_system_change_clears_standing_suppress()
    {
        var start = new DateTime(2100, 1, 1);
        var game = TestingUtilities.CreateTestUniverse(2, start, false);
        game.Settings.EnforceSingleThread = true;

        var systems = game.Systems.Distinct().ToArray();
        var home = systems[0];
        var remoteId = systems[1].ID;
        var star = home.GetFirstEntityWithDataBlob<StarInfoDB>();

        var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);

        var ship = Entity.Create(faction.Id);
        home.AddEntity(ship, new List<BaseDataBlob>
        {
            new ShipInfoDB(),
            new NameDB("Scout"),
            new PositionDB(0, 0, 0, star),
        });

        var fleet = Entity.Create(faction.Id);
        var fleetDB = new FleetDB();
        home.AddEntity(fleet, new List<BaseDataBlob>
        {
            fleetDB,
            new OrderableDB(),
            new PositionDB(0, 0, 0, star),
        });
        fleetDB.FlagShipID = ship.Id;
        fleetDB.AddChild(ship);

        fleetDB.StandingLastFlagshipSystemId = remoteId;
        fleetDB.StandingSuppressUntil = home.StarSysDateTime + TimeSpan.FromDays(1);
        fleetDB.StandingStatusMessage = "Can't find more survey targets";

        FleetStandingSystemSync.OnFlagshipSystemChanged(fleet, fleetDB);

        Assert.IsNull(fleetDB.StandingSuppressUntil);
        Assert.IsNull(fleetDB.StandingStatusMessage);
        Assert.AreEqual(-1, fleetDB.ActiveStandingOrderIndex);
        Assert.AreEqual(home.ID, fleetDB.StandingLastFlagshipSystemId);
    }
}
