using System;
using System.Collections.Generic;
using Pulsar4X.Orbital;
using Pulsar4X.Datablobs;
using Pulsar4X.Fleets;
using Pulsar4X.Damage;
using Pulsar4X.Names;
using Pulsar4X.Orbits;
using Pulsar4X.People;
using Pulsar4X.Engine;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;

namespace Pulsar4X.Ships
{
    public static class ShipFactory
    {
        /// <summary>
        /// new ship in a circular orbit at a distance of twice the parent bodies radius (size)
        /// </summary>
        /// <param name="shipDesign"></param>
        /// <param name="ownerFaction"></param>
        /// <param name="parent"></param>
        /// <param name="shipName"></param>
        /// <returns></returns>
        public static Entity CreateShip(ShipDesign shipDesign, Entity ownerFaction, Entity parent, string? shipName = null)
        {

            double distanceFromParent = parent.GetDataBlob<MassVolumeDB>().RadiusInM * 2;
            var pos = new Vector3(distanceFromParent, 0, 0);
            var orbit = OrbitDB.FromPosition(parent, pos, shipDesign.MassPerUnit, parent.StarSysDateTime);
            return CreateShip(shipDesign, ownerFaction, orbit, parent, shipName);
        }

        /// <summary>
        /// new ship in a circular orbit at twice the parent bodies radius (size), and a given true anomaly
        /// </summary>
        /// <param name="shipDesign"></param>
        /// <param name="ownerFaction"></param>
        /// <param name="parent"></param>
        /// <param name="angleRad">true anomaly</param>
        /// <param name="shipName"></param>
        /// <returns></returns>
        public static Entity CreateShip(ShipDesign shipDesign, Entity ownerFaction, Entity parent, double angleRad, string? shipName = null)
        {


            var distanceFromParent = parent.GetDataBlob<MassVolumeDB>().RadiusInM * 2;

            var x = distanceFromParent * Math.Cos(angleRad);
            var y = distanceFromParent * Math.Sin(angleRad);

            var pos = new Vector3(x, y, 0);
            var orbit = OrbitDB.FromPosition(parent, pos, shipDesign.MassPerUnit, parent.StarSysDateTime);
            return CreateShip(shipDesign, ownerFaction, orbit, parent, shipName);
        }

        /// <summary>
        /// new ship in a circular orbit at a given position from the parent.
        /// </summary>
        /// <param name="shipDesign"></param>
        /// <param name="ownerFaction"></param>
        /// <param name="position"></param>
        /// <param name="parent"></param>
        /// <param name="shipName"></param>
        /// <returns></returns>
        public static Entity CreateShip(ShipDesign shipDesign, Entity ownerFaction, Vector3 position, Entity parent, string? shipName = null)
        {
            var orbit = OrbitDB.FromPosition(parent, position, shipDesign.MassPerUnit, parent.StarSysDateTime);
            return CreateShip(shipDesign, ownerFaction, orbit, parent, shipName);
        }

        /// <summary>
        /// new ship with an orbit and position defined by kepler elements.
        /// </summary>
        /// <param name="shipDesign"></param>
        /// <param name="ownerFaction"></param>
        /// <param name="ke"></param>
        /// <param name="parent"></param>
        /// <param name="shipName"></param>
        /// <returns></returns>
        public static Entity CreateShip(ShipDesign shipDesign, Entity ownerFaction, KeplerElements ke, Entity parent, string? shipName = null)
        {
            OrbitDB orbit = OrbitDB.FromKeplerElements(parent, shipDesign.MassPerUnit, ke, parent.StarSysDateTime);
            var position = OrbitMath.GetPosition(ke, parent.StarSysDateTime);
            return CreateShip(shipDesign, ownerFaction, orbit, parent, shipName);
        }

        public static Entity CreateShip(ShipDesign shipDesign, Entity ownerFaction, OrbitDB orbit, Entity parent, string? shipName = null)
        {
            if (shipDesign.DesignVersion == 0) //we're using version 0 to indicate the design hasn't been built yet.
                shipDesign.DesignVersion = 1;

            if (parent?.Manager == null)
                throw new InvalidOperationException(
                    $"Cannot create ship '{shipName ?? shipDesign.Name}': orbit parent has no EntityManager (stale PlanetEntity after load?).");

            if (shipDesign.DamageProfileDB == null)
                throw new InvalidOperationException(
                    $"Cannot create ship '{shipName ?? shipDesign.Name}': design has no DamageProfileDB.");

            var starsys = parent.Manager;
            var position = OrbitMath.GetPosition(orbit, parent.StarSysDateTime);

            if (string.IsNullOrEmpty(shipName))
                shipName = NameFactory.GetShipName(ownerFaction.Manager?.Game ?? starsys.Game);

            var ship = Entity.Create();
            ship.FactionOwnerID = ownerFaction.Id;

            var namedb = new NameDB(ship.Id.ToString());
            namedb.SetName(ownerFaction.Id, shipName);

            // Name + orbit must be in the initial blob list so EntityAdded projection does not see
            // a half-built ship (AddEntity publishes before the old post-add SetDataBlob calls ran).
            var dataBlobs = new List<BaseDataBlob>
            {
                new ShipInfoDB(shipDesign),
                MassVolumeDB.NewFromMassAndVolume(shipDesign.MassPerUnit, shipDesign.VolumePerUnit),
                new PositionDB(position, parent),
                (EntityDamageProfileDB)shipDesign.DamageProfileDB.Clone(),
                new ComponentInstancesDB(),
                new OrderableDB(),
                namedb,
                orbit,
            };

            try
            {
                starsys.AddEntity(ship, dataBlobs);

                if (ship.Manager is null)
                    throw new InvalidOperationException(
                        $"AddEntity left ship#{ship.Id} without Manager — cannot install components.");
                if (!ship.TryGetDataBlob<ComponentInstancesDB>(out _))
                    throw new InvalidOperationException(
                        $"AddEntity left ship#{ship.Id} without ComponentInstancesDB.");

                foreach (var item in shipDesign.Components)
                {
                    if (item.design == null)
                        throw new InvalidOperationException(
                            $"Ship design '{shipDesign.Name}' has a null component entry.");
                    ship.AddComponent(item.design, item.count);
                }

                if (ship.HasDataBlob<NewtonThrustAbilityDB>())
                    NewtonionMovementProcessor.UpdateNewtonThrustAbilityDB(ship);

                return ship;
            }
            catch
            {
                // Don't leave a half-registered entity for the next hotloop to trip over.
                if (ship.Manager != null)
                    ship.Destroy();
                throw;
            }
        }

        public static void DestroyShip(Entity shipToDestroy)
        {
            // Steps:
            // - Remove the ship from fleet (if any)
            // - Remove the ship as the fleet flagship (if set)
            // - Kill any officers on board
            // - Create wreckage
            // - Remove the ship entity from the game

            var game = shipToDestroy.AttachedManager.Game;
            var faction = game.Factions[shipToDestroy.FactionOwnerID];

            // Remove the ship from its fleet
            if (faction.TryGetDataBlob<FleetDB>(out var fleetDB))
            {
                // Recursively try to get the fleet the ship belongs to
                var belongsToFleet = fleetDB.TryGetChild<FleetDB>(shipToDestroy);

                // If we found it send out the order to unassign the ship
                if (belongsToFleet != null && belongsToFleet.OwningEntity.IsValid)
                {
                    // The unassign ship command removes the ship from the fleet
                    // and checks if it is the flagship and removes that also
                    var command = FleetOrder.UnassignShip(
                        shipToDestroy.FactionOwnerID,
                        belongsToFleet.OwningEntity,
                        shipToDestroy);

                    game.OrderHandler.HandleOrder(command);
                }
            }

            // Kill any officers on board
            // (currently just the commander)
            // TODO: check for additional people on board (passengers, officers, scientists etc)
            if (shipToDestroy.TryGetDataBlob<ShipInfoDB>(out var shipInfoDB)
                && shipToDestroy.AttachedManager.TryGetEntityById(shipInfoDB.CommanderID, out var commanderEntity))
            {
                CommanderFactory.DestroyCommander(commanderEntity);
            }


            // Remove the ship entity from the game
            shipToDestroy.Destroy();
        }
    }
}