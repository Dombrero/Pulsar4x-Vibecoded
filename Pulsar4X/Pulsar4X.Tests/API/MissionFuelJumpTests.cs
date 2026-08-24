using System;
using System.Collections.Generic;
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
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class MissionFuelJumpTests : ApiTestBase
    {
        private ICargoable UnlockFuel(PlayerSession session)
        {
            var data = _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data;
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

        private (Entity fleet, Entity ship, Entity gate, ICargoable fuel) MakeFleetFarFromGate(
            PlayerSession session, long shipFuelUnits)
        {
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var fuel = UnlockFuel(session);

            var gate = system.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault();
            if (gate == null || !gate.IsValid)
            {
                gate = Entity.Create();
                system.AddEntity(gate, new List<BaseDataBlob>
                {
                    new NameDB("JP-Test"),
                    new PositionDB(new Vector3(5e11, 0, 0), star),
                    new JumpPointDB(),
                });
            }

            var jpDb = gate.GetDataBlob<JumpPointDB>();
            if (!jpDb.IsDiscovered.Contains(session.FactionId))
                jpDb.IsDiscovered.Add(session.FactionId);

            // Park the ship ~1 AU away from the gate so outbound need is non-trivial.
            if (!gate.TryGetDataBlob<PositionDB>(out var gatePos))
                throw new InvalidOperationException("gate needs PositionDB");
            var shipAbs = gatePos.AbsolutePosition + new Vector3(1.5e11, 0, 0);

            // Capacity must match unit count: EstimateWarpTankFuelUnits bills against full tank size.
            const long tankUnits = 100_000;
            double tankVolume = fuel.VolumePerUnit * tankUnits;
            var shipStorage = new CargoStorageDB(fuel.CargoTypeID, tankVolume)
            {
                TransferRate = 5000,
                TransferRangeDv_mps = 1e12,
            };
            if (shipFuelUnits > 0)
                shipStorage.AddCargoByUnit(fuel, Math.Min(shipFuelUnits, tankUnits));

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                shipStorage,
                new PositionDB(shipAbs, star) { MoveType = PositionDB.MoveTypes.Warp },
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Jumper", session.FactionId, "Jumper"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB
                {
                    MaxSpeed = 1e9,
                    EnergyType = fuel.UniqueID,
                    BubbleCreationCost = 1,
                    BubbleSustainCost = 0,
                },
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = fuel,
                    MaxOutputFromReactor = 1000,
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID!] = 1e15 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID!] = 1e15 },
                },
                new NewtonThrustAbilityDB(fuel.UniqueID!)
                {
                    ExhaustVelocity = 3000,
                    ThrustInNewtons = 1000,
                    FuelBurnRate = 1,
                },
            });

            var fleet = Entity.Create(session.FactionId);
            var fleetDB = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDB,
                new OrderableDB(),
                new NameDB("Jump Fleet", session.FactionId, "Jump Fleet"),
            });
            fleetDB.FlagShipID = ship.Id;
            fleetDB.AddChild(ship);

            return (fleet, ship, gate, fuel);
        }

        [Test]
        public void EvaluateJump_empty_tanks_cannot_afford_round_trip()
        {
            var session = Connect();
            var (_, ship, gate, _) = MakeFleetFarFromGate(session, shipFuelUnits: 0);

            var a = MissionFuelEstimator.EvaluateJumpViaGate(ship, gate);
            Assert.That(a.CanAffordOutbound, Is.False);
            Assert.That(a.CanAffordRoundTrip, Is.False);
            Assert.That(a.Reason, Does.Contain("empty").IgnoreCase
                .Or.Contain("Need").IgnoreCase);
        }

        [Test]
        public void EvaluateJump_full_tanks_can_afford_round_trip()
        {
            var session = Connect();
            var (_, ship, gate, _) = MakeFleetFarFromGate(session, shipFuelUnits: 100_000);

            var a = MissionFuelEstimator.EvaluateJumpViaGate(ship, gate);
            Assert.That(a.CanAffordOutbound, Is.True, a.Reason);
            Assert.That(a.CanAffordRoundTrip, Is.True, a.Reason);
            Assert.That(a.RoundTripNeedUnits, Is.GreaterThan(0));
            Assert.That(a.StoredUnits, Is.GreaterThanOrEqualTo(a.RoundTripNeedUnits));
        }

        [Test]
        public void EvaluateJump_partial_fuel_may_afford_outbound_but_not_return()
        {
            var session = Connect();
            var (_, ship, gate, fuel) = MakeFleetFarFromGate(session, shipFuelUnits: 100_000);

            var full = MissionFuelEstimator.EvaluateJumpViaGate(ship, gate);
            Assert.That(full.CanAffordRoundTrip, Is.True, full.Reason);
            Assert.That(full.OutboundNeedUnits, Is.GreaterThan(0));

            // Leave enough for one hop but not 2×.
            long keep = Math.Max(1, full.OutboundNeedUnits);
            var storage = ship.GetDataBlob<CargoStorageDB>();
            long stored = storage.GetUnitsStored(fuel, includeEscro: false);
            long remove = stored - keep;
            if (remove > 0)
                CargoTransferProcessor.AddRemoveCargoMass(ship, fuel, -remove * fuel.MassPerUnit);

            var partial = MissionFuelEstimator.EvaluateJumpViaGate(ship, gate);
            Assert.That(partial.CanAffordOutbound, Is.True, partial.Reason);
            Assert.That(partial.CanAffordRoundTrip, Is.False, "Must require return reserve");
            Assert.That(partial.Reason, Does.Contain("return").IgnoreCase);
        }

        [Test]
        public void JumpCommand_rejected_when_fleet_cannot_afford_fuel()
        {
            var session = Connect();
            var (fleet, _, gate, _) = MakeFleetFarFromGate(session, shipFuelUnits: 0);

            var result = _server.SubmitCommand(session, new JumpCommand(fleet.Id, gate.Id));
            Assert.That(result.Accepted, Is.False, "Empty tanks must reject jump");
            Assert.That(result.RejectionReason, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void JumpCommand_accepted_when_fleet_has_round_trip_fuel()
        {
            var session = Connect();
            var (fleet, _, gate, _) = MakeFleetFarFromGate(session, shipFuelUnits: 100_000);

            var result = _server.SubmitCommand(session, new JumpCommand(fleet.Id, gate.Id));
            Assert.That(result.Accepted, Is.True, result.RejectionReason);
        }

        [Test]
        public void ThrustView_includes_mission_fuel_warning_when_low()
        {
            var session = Connect();
            var (_, ship, _, _) = MakeFleetFarFromGate(session, shipFuelUnits: 0);

            var snap = _projector.ProjectEntity(ship, session.FactionId);
            var thrust = snap?.GetView<ThrustView>();
            Assert.That(thrust, Is.Not.Null);
            Assert.That(thrust!.MissionFuelWarning, Is.Not.Null.And.Not.Empty);
        }
    }
}
