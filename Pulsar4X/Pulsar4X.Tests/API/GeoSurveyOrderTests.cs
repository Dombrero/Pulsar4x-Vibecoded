using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Messaging;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class GeoSurveyOrderTests : ApiTestBase
    {
        [Test]
        public void GeoSurvey_from_earth_does_not_progress_mars_until_arrived()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var earth = Entity.Create();
            system.AddEntity(earth, new List<BaseDataBlob>
            {
                new NameDB("Earth", session.FactionId, "Earth"),
                new PositionDB(new Vector3(1.5e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 5.972e24,
                    1.5e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
            });

            var mars = Entity.Create();
            system.AddEntity(mars, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(6.4e23, 3.4e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 6.4e23,
                    2.3e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 1000 },
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8 },
                new GeoSurveyAbilityDB { Speed = 50 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Survey Fleet", session.FactionId, "Survey Fleet"),
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var survey = GeoSurveyOrder.CreateCommand(session.FactionId, fleet, mars);
            Assert.That(QueueOrder(survey), Is.True);

            // Orderable pulse while still at Earth: travel may start, survey must not complete points.
            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(fleet, 0);
            processor.ProcessEntity(fleet, (int)TimeSpan.FromDays(2).TotalSeconds);

            var geo = mars.GetDataBlob<GeoSurveyableDB>();
            bool started = geo.HasSurveyStarted(session.FactionId);
            if (started)
            {
                Assert.That(geo.GeoSurveyStatus[session.FactionId], Is.EqualTo(geo.PointsRequired),
                    "Survey points must not decrease while the fleet is still at Earth.");
            }

            Assert.That(FleetOrderCleanup.IsFleetAtBody(fleet, mars), Is.False);
            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList.OfType<GeoSurveyOrder>().Any(), Is.True,
                "Geo survey order remains until the body is surveyed.");
            Assert.That(survey.Name, Does.Contain("en route"));
        }

        [Test]
        public void Ship_near_body_but_parented_to_star_counts_as_at_body()
        {
            // Legacy micro-hop state: absolute position at Mars, PositionDB parent still the star.
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var mars = Entity.Create();
            system.AddEntity(mars, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star) { MoveType = PositionDB.MoveTypes.Orbit },
                MassVolumeDB.NewFromMassAndRadius_m(6.4e23, 3.4e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 6.4e23,
                    2.3e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
            });

            var lowOrbit = OrbitMath.LowOrbitRadius(mars);
            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                // Parent = star (bad warp exit), but absolute pos inside Mars SOI.
                new PositionDB(mars.GetDataBlob<PositionDB>().AbsolutePosition + new Vector3(lowOrbit, 0, 0), star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
            });

            Assert.That(ship.GetDataBlob<PositionDB>().Parent?.Id, Is.EqualTo(star.Id));
            Assert.That(FleetOrderCleanup.IsShipAtBody(ship, mars), Is.True,
                "SOI proximity must count as on-station so survey does not keep micro-warping.");
        }

        [Test]
        public void GeoSurvey_at_target_progresses_points()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var mars = Entity.Create();
            system.AddEntity(mars, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(6.4e23, 3.4e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 6.4e23,
                    2.3e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 100 },
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(mars.GetDataBlob<PositionDB>().AbsolutePosition, mars),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8 },
                new GeoSurveyAbilityDB { Speed = 40 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Survey Fleet", session.FactionId, "Survey Fleet"),
                new PositionDB(mars.GetDataBlob<PositionDB>().AbsolutePosition, mars),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var survey = GeoSurveyOrder.CreateCommand(session.FactionId, fleet, mars);
            QueueOrder(survey);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            DateTime t0 = system.StarSysDateTime;
            processor.ProcessEntity(fleet, t0);
            processor.ProcessEntity(fleet, t0 + TimeSpan.FromDays(1));

            var geo = mars.GetDataBlob<GeoSurveyableDB>();
            Assert.That(geo.HasSurveyStarted(session.FactionId), Is.True);
            Assert.That(geo.GeoSurveyStatus[session.FactionId], Is.EqualTo(60),
                "40 survey points should apply after one day on station.");
        }

        [Test]
        public void GeoSurvey_progress_pushes_entity_changed_for_body()
        {
            var session = Connect();
            int marsId = -1;
            var received = new List<Message>();
            Task Handler(Message m)
            {
                received.Add(m);
                return Task.CompletedTask;
            }
            MessagePublisher.Instance.Subscribe(MessageTypes.EntityChanged, Handler, m => m.EntityId == marsId);

            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var mars = Entity.Create();
            system.AddEntity(mars, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(6.4e23, 3.4e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 6.4e23,
                    2.3e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 100 },
            });
            marsId = mars.Id;

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(mars.GetDataBlob<PositionDB>().AbsolutePosition, mars),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8 },
                new GeoSurveyAbilityDB { Speed = 40 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Survey Fleet", session.FactionId, "Survey Fleet"),
                new PositionDB(mars.GetDataBlob<PositionDB>().AbsolutePosition, mars),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var survey = GeoSurveyOrder.CreateCommand(session.FactionId, fleet, mars);
            QueueOrder(survey);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            DateTime t0 = system.StarSysDateTime;
            processor.ProcessEntity(fleet, t0);
            processor.ProcessEntity(fleet, t0 + TimeSpan.FromDays(1));

            bool receivedEntityChanged = SpinWait.SpinUntil(() =>
                received.Any(evt => evt.EntityId == mars.Id),
                millisecondsTimeout: 1000);

            MessagePublisher.Instance.Unsubscribe(MessageTypes.EntityChanged, Handler);

            Assert.That(receivedEntityChanged, Is.True,
                "Survey progress must push EntityChanged so the client refreshes the body's survey UI.");
        }
    }
}
