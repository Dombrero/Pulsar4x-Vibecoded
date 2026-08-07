using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests;

/// <summary>
/// Headless runtime stress: load a real save, advance time, issue move/survey,
/// save/load again — catches Goal/Standing/serialize crashes.
/// </summary>
[TestFixture]
public class RuntimeStabilityStressTests : ApiTestBase
{
    private static readonly string UserSavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pulsar4X", "Pulsar4X", "Saves",
        "goal integration test 1 - 2050-01-08_20-00-00.sav");

    private Entity MakeShip(PlayerSession session)
    {
        var ship = Entity.Create(session.FactionId);
        var data = _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data;
        var energyGood = data.CargoGoods.GetAll().Values.Concat(data.LockedCargoGoods.GetAll().Values).First();
        var thrustAbility = new NewtonThrustAbilityDB("test-fuel")
        {
            ThrustInNewtons = 100000,
            ExhaustVelocity = 3000,
            FuelBurnRate = 1,
        };

        _game.Systems[0].AddEntity(ship, new List<BaseDataBlob>
        {
            new PositionDB { AbsolutePosition = new Vector3(1.5e11, 0, 0) },
            new MassVolumeDB { MassDry = 10000 },
            new NameDB("Stress Ship", session.FactionId, "Stress Ship"),
            new OrderableDB(),
            new ShipInfoDB(),
            new WarpAbilityDB { MaxSpeed = 100000, EnergyType = energyGood.UniqueID },
            new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
            {
                EnergyType = energyGood,
                EnergyStored = new Dictionary<string, double> { [energyGood.UniqueID] = 1e9 },
                EnergyStoreMax = new Dictionary<string, double> { [energyGood.UniqueID] = 1e9 },
            },
            thrustAbility,
        });
        thrustAbility.SetFuel(2000, 12000);

        var star = _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>();
        ship.SetDataBlob(OrbitDB.FromAsteroidFormat_r(
            star, star.GetDataBlob<MassVolumeDB>().MassTotal, 12000,
            semiMajorAxis_m: 1.5e11, eccentricity: 0, inclination: 0,
            longitudeOfAscendingNode: 0, argumentOfPeriapsis: 0, meanAnomaly: 0,
            epoch: _game.Systems[0].StarSysDateTime));
        return ship;
    }

    [Test]
    public void UserSave_Loads_And_TimeSteps_WithoutThrowing()
    {
        if (!File.Exists(UserSavePath))
            Assert.Ignore($"Save not found: {UserSavePath}");

        Game loaded = null!;
        Assert.DoesNotThrow(() => loaded = Game.Load(File.ReadAllText(UserSavePath)),
            "Game.Load must not throw");

        loaded.Settings.EnforceSingleThread = true;
        loaded.TimePulse.Ticklength = TimeSpan.FromHours(6);

        for (int i = 0; i < 20; i++)
        {
            Assert.DoesNotThrow(() => loaded.TimePulse.TimeStep(), $"TimeStep #{i} threw");
            Assert.That(loaded.TimePulse.IsRunning, Is.False);
        }

        string roundTrip = null!;
        Assert.DoesNotThrow(() => roundTrip = Game.Save(loaded), "Game.Save after timesteps");
        Assert.DoesNotThrow(() => Game.Load(roundTrip), "round-trip Game.Load");
    }

    [Test]
    public void MoveGeoStanding_TimeStep_SaveLoad()
    {
        var session = Connect();
        var ship = MakeShip(session);
        var fleet = Entity.Create(session.FactionId);
        var fleetDB = new FleetDB();
        fleetDB.Children.Add(ship);
        fleetDB.FlagShipID = ship.Id;
        _game.Systems[0].AddEntity(fleet, new List<BaseDataBlob>
        {
            new NameDB("Stress Fleet", session.FactionId, "Stress Fleet"),
            new OrderableDB(),
            fleetDB,
        });

        int bodyId = ProjectSystem(session).Entities
            .First(e => e.Kind != BodyKind.Star && e.GetView<OrbitView>() != null)
            .Id;
        Assert.That(_game.Systems[0].TryGetEntityById(bodyId, out var body), Is.True);

        var move = MoveToSystemBodyOrder.CreateCommand(session.FactionId, fleet, body);
        Assert.That(OrderEnqueue.Issued(_game, move), Is.True);

        _game.TimePulse.Ticklength = TimeSpan.FromHours(1);
        for (int i = 0; i < 8; i++)
            Assert.DoesNotThrow(() => _game.TimePulse.TimeStep(), $"move timestep #{i}");

        fleet.GetDataBlob<OrderableDB>().ActionList.Clear();

        if (body.HasDataBlob<GeoSurveyableDB>())
        {
            var geo = GeoSurveyOrder.CreateCommand(session.FactionId, fleet, body);
            Assert.That(OrderEnqueue.Issued(_game, geo), Is.True);
            for (int i = 0; i < 8; i++)
                Assert.DoesNotThrow(() => _game.TimePulse.TimeStep(), $"geo timestep #{i}");
        }

        Assert.DoesNotThrow(
            () => _game.ProcessorManager.RunProcessOnEntity<FleetDB>(fleet, 0),
            "FleetOrderProcessor crashed");

        string json = Game.Save(_game);
        var reloaded = Game.Load(json);
        reloaded.Settings.EnforceSingleThread = true;
        reloaded.TimePulse.Ticklength = TimeSpan.FromHours(1);
        Assert.DoesNotThrow(() => reloaded.TimePulse.TimeStep());
    }

    [Test]
    public void AssignGoal_DoesNotCrash_WhenPlannerFailsSafely()
    {
        var session = Connect();
        var ship = MakeShip(session);

        Assert.DoesNotThrow(() => AgentProcessor.AssignGoal(ship, new Goal
        {
            Type = GoalType.MoveTo,
            TargetEntityID = -99999,
        }));

        Assert.That(ship.TryGetDataBlob<GoalsDB>(out var goals), Is.True);
        Assert.That(goals!.GivenGoal, Is.Not.Null);
        Assert.That(goals.GivenGoal!.Status, Is.EqualTo(GoalStatus.Failed));
    }
}
