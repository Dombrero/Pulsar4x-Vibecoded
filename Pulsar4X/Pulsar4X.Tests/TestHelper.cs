using System.Collections.Generic;
using Pulsar4X.Blueprints;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Galaxy;
using Pulsar4X.Names;
using Pulsar4X.People;

namespace Pulsar4X.Tests
{
    internal class TestHelper
    {
        protected Game _game = null;
        protected EntityManager _entityManager = null;
        protected Dictionary<string, GasBlueprint> _gasDictionary = null;
        protected SpeciesDB _humans = null;
        protected AtmosphereDB _atmosphere;

        protected Entity GetPlanet(float baseTemperature, float albedo, double gravity, AtmosphereDB? atmosphere = null)
        {
            SystemBodyInfoDB planetBodyDB = new()
            {
                BodyType = BodyType.Terrestrial,
                SupportsPopulations = true,
                Gravity = gravity,
                BaseTemperature = baseTemperature,
                Albedo = new PercentValue(albedo)
            };
            NameDB planetNameDB = new("Test Planet");

            var blobs = new List<BaseDataBlob> { planetBodyDB, planetNameDB };
            if (atmosphere != null)
                blobs.Add(atmosphere);

            var result = Entity.Create();
            _entityManager.AddEntity(result, blobs);

            return result;
        }
    }
}