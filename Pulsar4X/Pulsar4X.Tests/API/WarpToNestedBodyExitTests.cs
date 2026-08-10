using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Storage;
using WarpMoveCommand = Pulsar4X.Movement.WarpMoveCommand;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Guards AU-scale wrong-frame warp exits to nested bodies (moons etc.) that made
    /// GeoSurvey / nearest-body orders re-warp to the same target forever.
    /// </summary>
    [TestFixture]
    public class WarpToNestedBodyExitTests : ApiTestBase
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

        private (Entity planet, Entity moon, Entity ship, ICargoable fuel) MakePlanetMoonShip(
            PlayerSession session)
        {
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var now = system.StarSysDateTime;
            var fuel = UnlockFuel(session);

            var planet = Entity.Create();
            system.AddEntity(planet, new List<BaseDataBlob>
            {
                new NameDB("ParentPlanet", session.FactionId, "ParentPlanet"),
                new PositionDB(new Vector3(1.5e11, 0, 0), star) { MoveType = PositionDB.MoveTypes.Orbit },
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 5.972e24,
                    1.5e11, 0, 0, 0, 0, 0, now),
            });

            var moon = Entity.Create();
            system.AddEntity(moon, new List<BaseDataBlob>
            {
                new NameDB("NestedMoon", session.FactionId, "NestedMoon"),
                new PositionDB(new Vector3(3.84e8, 0, 0), planet) { MoveType = PositionDB.MoveTypes.Orbit },
                MassVolumeDB.NewFromMassAndRadius_m(7.342e22, 1.737e6),
                OrbitDB.FromAsteroidFormat_r(
                    planet, planet.GetDataBlob<MassVolumeDB>().MassTotal, 7.342e22,
                    3.84e8, 0, 0, 0, 0, 0, now),
                new GeoSurveyableDB { PointsRequired = 100 },
            });

            var storage = new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            storage.AddCargoByUnit(fuel, 1_900_000);

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                storage,
                new PositionDB(new Vector3(1e7, 0, 0), planet),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor Nested", session.FactionId, "Surveyor Nested"),
                new OrderableDB(),
                new ShipInfoDB(),
                // Realistic surveyor warp (~25 km/s) — 1e8 hid the nested period-sweep detour.
                new WarpAbilityDB { MaxSpeed = 25712, EnergyType = fuel.UniqueID },
                new GeoSurveyAbilityDB { Speed = 50 },
                new EnergyGenAbilityDB(now)
                {
                    EnergyType = fuel,
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID] = 1e15 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID] = 1e15 },
                },
            });

            return (planet, moon, ship, fuel);
        }

        [Test]
        public void Warp_exit_to_nested_moon_stays_near_moon_not_au_away()
        {
            var session = Connect();
            var (_, moon, ship, _) = MakePlanetMoonShip(session);
            var now = ship.StarSysDateTime;
            var shipAbs = ship.GetDataBlob<PositionDB>().AbsolutePosition;
            var moonAbs = moon.GetDataBlob<PositionDB>().AbsolutePosition;

            var cmd = WarpMoveCommand.CreateCommandEZ(ship, moon, now);
            Assert.That(OrderEnqueue.Enqueue(ship.AttachedManager.Game, cmd), Is.True);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(ship, 0);

            Assert.That(ship.TryGetDataBlob<WarpMovingDB>(out var warp), Is.True);
            double hop = (warp!.ExitPointAbsolute - shipAbs).Length();
            var moonAtExit = (Vector3)MoveMath.GetAbsoluteFuturePosition(moon, warp.PredictedExitTime);
            double exitToMoon = (warp.ExitPointAbsolute - moonAtExit).Length();

            Assert.That(exitToMoon, Is.LessThan(5e8),
                $"Exit must be near the moon at ETI (err={exitToMoon / 1.496e11:0.###} AU)");
            Assert.That(hop, Is.LessThan(5e9),
                $"Planet→moon hop must be << 1 AU (was {hop / 1.496e11:0.###} AU)");
        }

        [Test]
        public void Warp_arrival_at_nested_moon_parents_to_moon_so_survey_does_not_loop()
        {
            var session = Connect();
            var (planet, moon, ship, _) = MakePlanetMoonShip(session);
            var now = ship.StarSysDateTime;

            var cmd = WarpMoveCommand.CreateCommandEZ(ship, moon, now);
            Assert.That(OrderEnqueue.Enqueue(ship.AttachedManager.Game, cmd), Is.True);

            var orderable = new OrderableProcessor();
            orderable.Init(_game);
            orderable.ProcessEntity(ship, 0);
            Assert.That(ship.TryGetDataBlob<WarpMovingDB>(out var warp), Is.True);

            // Instant-arrive: finish exactly at PredictedExitTime so SOI matches Exit.
            warp!.LastProcessDateTime = now - TimeSpan.FromDays(1);
            WarpMoveProcessor.ProcessEntity(ship, warp.PredictedExitTime);

            Assert.That(FleetOrderCleanup.IsShipAtBody(ship, moon), Is.True,
                "After warp arrival the hull must count as at the moon (not stuck on the planet/star).");
            Assert.That(ship.GetDataBlob<PositionDB>().Parent?.Id, Is.EqualTo(moon.Id));

            // GeoSurvey must not enqueue another warp while already on-station.
            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            ship.AttachedManager.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Survey Fleet", session.FactionId, "Survey Fleet"),
                new PositionDB(new Vector3(0, 0, 0), planet),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var survey = new GeoSurveyOrder(ship, moon)
            {
                RequestingFactionGuid = session.FactionId,
                EntityCommandingGuid = ship.Id,
                Source = OrderSource.Standing,
                UseActionLanes = true,
            };
            survey.Execute(ship.StarSysDateTime);
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<WarpMoveCommand>().Any(c => !c.IsFinished()),
                Is.False,
                "On-station geo survey must not keep issuing warp-to-same-body.");
            Assert.That(survey.Name, Does.Contain("Geo Survey").IgnoreCase);
            Assert.That(survey.Name, Does.Not.Contain("en route").IgnoreCase);
        }
    }
}
