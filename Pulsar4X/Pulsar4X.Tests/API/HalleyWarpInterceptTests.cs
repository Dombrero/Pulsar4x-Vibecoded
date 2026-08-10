using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class HalleyWarpInterceptTests : ApiTestBase
    {
        [Test]
        public void HalleyLikeComet_intercept_stays_near_body_not_tens_of_thousands_AU()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var starPos = star.GetDataBlob<PositionDB>().AbsolutePosition;
            double starMass = star.GetDataBlob<MassVolumeDB>().MassDry;

            // Halley-class orbit: ~17.8 AU SMA, e≈0.967, ~75 year period.
            double sma_m = 2.6679e12;
            double eccentricity = 0.96714;
            var epoch = system.StarSysDateTime;

            var comet = Entity.Create();
            var orbit = OrbitDB.FromAsteroidFormat_r(
                star, starMass, 2.2e14,
                sma_m, eccentricity,
                Math.PI, // retrograde like Halley
                58.42 * Math.PI / 180,
                111.33 * Math.PI / 180,
                38.38 * Math.PI / 180,
                epoch);

            system.AddEntity(comet, new List<BaseDataBlob>
            {
                new NameDB("Halleys Comet", session.FactionId, "Halleys Comet"),
                new PositionDB(orbit.GetPosition(epoch), star) { MoveType = PositionDB.MoveTypes.Orbit },
                MassVolumeDB.NewFromMassAndRadius_m(2.2e14, 11000),
                orbit,
            });

            // Surveyor near Earth (~1 AU from Sol).
            var ship = Entity.Create(session.FactionId);
            var shipAbs = starPos + new Vector3(1.496e11, 0, 0);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(shipAbs - starPos, star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 5", session.FactionId, "Surveyor 5"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 120_000 }, // 120 km/s
            });

            var (exit, eti) = WarpMath.GetInterceptPosition(ship, comet, ship.StarSysDateTime);
            var bodyAtEti = OrbitMath.GetAbsolutePosition(orbit, eti);
            double missAu = (exit - bodyAtEti).Length() / 1.495978707e11;
            double hopAu = (exit - shipAbs).Length() / 1.495978707e11;

            Assert.That(missAu, Is.LessThan(0.5),
                $"Exit must meet the comet, not a ghost point (miss={missAu:0.###} AU)");
            Assert.That(hopAu, Is.LessThan(100),
                $"Hop must be solar-system scale, not period-sweep garbage (hop={hopAu:0} AU)");
            Assert.That((eti - ship.StarSysDateTime).TotalDays, Is.LessThan(365 * 50),
                "Travel time must be finite and less than decades for 120 km/s in-system");
        }
    }
}
