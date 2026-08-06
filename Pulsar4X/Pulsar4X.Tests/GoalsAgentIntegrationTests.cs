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
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests;

[TestFixture]
public class GoalsAgentIntegrationTests : ApiTestBase
{
    private Entity MakeGoalShip(PlayerSession session)
    {
        var ship = Entity.Create(session.FactionId);
        var data = _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data;
        var energyGood = data.CargoGoods.GetAll().Values.Concat(data.LockedCargoGoods.GetAll().Values).First();
        var thrustAbility = new Pulsar4X.Movement.NewtonThrustAbilityDB("test-fuel")
        {
            ThrustInNewtons = 100000,
            ExhaustVelocity = 3000,
            FuelBurnRate = 1,
        };

        _game.Systems[0].AddEntity(ship, new System.Collections.Generic.List<BaseDataBlob>
        {
            new Pulsar4X.Movement.PositionDB { AbsolutePosition = new Vector3(1.5e11, 0, 0) },
            new MassVolumeDB { MassDry = 10000 },
            new NameDB("Goal Ship", session.FactionId, "Goal Ship"),
            new OrderableDB(),
            new ShipInfoDB(),
            new Pulsar4X.Movement.WarpAbilityDB { MaxSpeed = 100000, EnergyType = energyGood.UniqueID },
            new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
            {
                EnergyType = energyGood,
                EnergyStored = new System.Collections.Generic.Dictionary<string, double> { [energyGood.UniqueID] = 1e9 },
                EnergyStoreMax = new System.Collections.Generic.Dictionary<string, double> { [energyGood.UniqueID] = 1e9 },
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
    public void AssignMoveToGoal_OnShip_QueuesWarpOrCompletes()
    {
        var session = Connect();
        var ship = MakeGoalShip(session);
        int bodyId = ProjectSystem(session).Entities
            .First(e => e.Kind != BodyKind.Star && e.GetView<OrbitView>() != null)
            .Id;

        AgentProcessor.AssignGoal(ship, new Goal
        {
            Type = GoalType.MoveTo,
            TargetEntityID = bodyId,
        });

        Assert.That(ship.TryGetDataBlob<GoalsDB>(out var goals), Is.True);
        Assert.That(goals!.GivenGoal, Is.Not.Null);
        Assert.That(goals.GivenGoal!.Status,
            Is.EqualTo(GoalStatus.Active).Or.EqualTo(GoalStatus.Completed).Or.EqualTo(GoalStatus.Failed));

        if (goals.GivenGoal.Status == GoalStatus.Failed)
            Assert.Fail($"MoveTo goal failed: {goals.GivenGoal.Message}");

        if (goals.GivenGoal.Status == GoalStatus.Active)
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionsFor(goals.GivenGoal).Count, Is.GreaterThan(0));
    }

    [Test]
    public void MoveToBodyCommand_UsesGoalsPipeline()
    {
        var session = Connect();
        var ship = MakeGoalShip(session);
        int bodyId = ProjectSystem(session).Entities
            .First(e => e.Kind != BodyKind.Star && e.GetView<OrbitView>() != null)
            .Id;

        var result = _server.SubmitCommand(session, new MoveToBodyCommand(ship.Id, bodyId));
        Assert.That(result.Accepted, Is.True, result.RejectionReason);

        Assert.That(ship.TryGetDataBlob<GoalsDB>(out var goals), Is.True);
        Assert.That(goals!.GivenGoal, Is.Not.Null);
        Assert.That(goals.GivenGoal!.Type, Is.EqualTo(GoalType.MoveTo));
        Assert.That(goals.GivenGoal.TargetEntityID, Is.EqualTo(bodyId));
    }

    [Test]
    public void AssignMoveToGoal_OnFleet_HandsDownToShips()
    {
        var session = Connect();
        var ship = MakeGoalShip(session);
        var fleet = Entity.Create(session.FactionId);
        var fleetDB = new FleetDB();
        fleetDB.Children.Add(ship);
        fleetDB.FlagShipID = ship.Id;
        _game.Systems[0].AddEntity(fleet, new System.Collections.Generic.List<BaseDataBlob>
        {
            new NameDB("Goal Fleet", session.FactionId, "Goal Fleet"),
            new OrderableDB(),
            fleetDB,
        });

        int bodyId = ProjectSystem(session).Entities
            .First(e => e.Kind != BodyKind.Star && e.GetView<OrbitView>() != null)
            .Id;

        AgentProcessor.AssignGoal(fleet, new Goal
        {
            Type = GoalType.MoveTo,
            TargetEntityID = bodyId,
        });

        Assert.That(fleet.TryGetDataBlob<GoalsDB>(out var fleetGoals), Is.True);
        Assert.That(fleetGoals!.GivenGoal, Is.Not.Null);
        Assert.That(fleetGoals.GivenGoal!.Status,
            Is.EqualTo(GoalStatus.Active).Or.EqualTo(GoalStatus.Completed));

        Assert.That(ship.TryGetDataBlob<GoalsDB>(out var shipGoals), Is.True);
        Assert.That(shipGoals!.GivenGoal, Is.Not.Null);
        Assert.That(shipGoals.GivenGoal!.ParentGoalId, Is.EqualTo(fleetGoals.GivenGoal.Id));
    }
}
