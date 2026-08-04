using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Pulsar4X.Engine.Auth;
using Pulsar4X.Modding;
using Pulsar4X.DataStructures;
using Pulsar4X.Blueprints;
using Pulsar4X.Interfaces;
using Pulsar4X.Engine.Orders;
using System.Runtime.CompilerServices;
using Pulsar4X.Events;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.Energy;
using Pulsar4X.JumpPoints;
using Pulsar4X.Sensors;
using Pulsar4X.Logistics;
using Pulsar4X.Messaging;
[assembly: InternalsVisibleTo("Pulsar4X.Tests")]

namespace Pulsar4X.Engine
{
    public class Game
    {
        /// <summary>
        /// Entities like Suns, Planets, Asteroids etc are considered Neutral
        /// and should have their FactionOwnerID set equal to NeutralFactionID
        /// </summary>
        public static readonly int NeutralFactionId = -99;

        [JsonProperty]
        public string? Name { get; set; }
        [JsonProperty]
        public string? CreatedOnGitHash { get; set; }
        [JsonProperty]
        public string? LastSaveGitHash { get; set; }
        [JsonProperty]
        public MasterTimePulse? TimePulse { get; internal set; }
        [JsonProperty]
        public SafeDictionary<string, ThemeBlueprint> Themes { get; internal set; } = new();

        [JsonProperty]
        public SafeDictionary<string, GasBlueprint> AtmosphericGases { get; internal set; } = new();

        [JsonProperty]
        public SafeDictionary<string, TechCategoryBlueprint> TechCategories { get; internal set; } = new();

        [JsonProperty]
        public SystemGenSettingsBlueprint? SystemGenSettings { get; internal set; }
        /// <summary>
        /// List of StarSystems currently in the game.
        /// </summary>
        [JsonProperty]
        public List<StarSystem> Systems { get; internal set; } = new();

        [JsonProperty]
        public EntityManager? GlobalManager { get; internal set; }
        [JsonProperty]
        internal readonly SafeDictionary<string, EntityManager> GlobalManagerDictionary = new();

        [JsonIgnore]
        internal ProcessorManager? ProcessorManager { get; set; }
        [JsonProperty]
        public Player SpaceMaster { get; internal set; } = new Player("Space Master", "");

        [JsonProperty]
        public List<Player> Players { get; internal set; } = new List<Player>();

        [JsonIgnore]
        public IOrderHandler? OrderHandler { get; internal set; }
        [JsonProperty]
        public Entity GameMasterFaction { get; internal set; } = Entity.InvalidEntity;
        [JsonProperty]
        public GameSettings? Settings { get; internal set; }
        [JsonProperty]
        public ModDataStore? StartingGameData { get; set; }

        [JsonProperty]
        public GalaxyFactory? GalaxyGen { get; set; }

        [JsonProperty]
        public Dictionary<int, Entity> Factions { get; } = new();

        /// <summary>
        /// Tracks the next available faction mask index (0-31).
        /// Used by FactionFactory when creating new factions.
        /// </summary>
        [JsonProperty]
        internal int NextFactionMaskIndex { get; set; } = 0;

        /// <summary>
        /// Maximum number of factions supported by the mask system (32 bits in an int).
        /// </summary>
        public const int MaxFactions = 32;

        // Cargoable / design IDs (not entity IDs). Must round-trip through save/load.
        [JsonProperty]
        private int EntityIDCounterValue
        {
            get => EntityIDCounter;
            set => EntityIDCounter = value;
        }

        // Real entity IDs. Expression-bodied get-only used to ignore the saved value on load,
        // leaving the static counter at 0 → "Entity with ID N already exists" on the next spawn.
        [JsonProperty]
        private int NextEntityID
        {
            get => EntityIDGenerator.NextId;
            set => EntityIDGenerator.NextId = value;
        }

        private static int EntityIDCounter = 0;

        internal Random RNG => GlobalManager.RNG;

        public Game() { }

        /// <summary>
        /// Clears all static/singleton state to prepare for a new game session.
        /// This must be called before creating a new game to prevent state leakage
        /// from a previous game session.
        /// </summary>
        internal static void ClearGlobalState()
        {
            EventManager.Instance.Clear();
            MessagePublisher.Instance.Clear();
            LogisticsCycle.Clear();
            EntityIDGenerator.Reset(0);
            EntityIDCounter = 0;
        }

        public Game(NewGameSettings settings, ModDataStore modDataStore)
        {
            ClearGlobalState();
            ApplyModData(modDataStore);
            ApplySettings(settings);

            if (SystemGenSettings == null) throw new ArgumentNullException("SystemGenSettings cannot be null");
            if (Settings == null) throw new ArgumentNullException("Settings cannot be null");

            TimePulse = new(this);
            TimePulse.Initialize(this);
            ProcessorManager = new ProcessorManager(this);
            OrderHandler = new StandAloneOrderHandler(this);
            GlobalManager = new EntityManager();
            GlobalManager.Initialize(this, settings.MasterSeed);
            GameMasterFaction = FactionFactory.CreateSpaceMasterFaction(this, SpaceMaster, "SpaceMaster Faction");
            GalaxyGen = new GalaxyFactory(SystemGenSettings);
        }

        public void ApplySettings(NewGameSettings settings)
        {
            Settings = settings;
        }

        public void ApplyModData(ModDataStore modDataStore)
        {
            StartingGameData = modDataStore;
            Themes = new SafeDictionary<string, ThemeBlueprint>(modDataStore.Themes);
            AtmosphericGases = new SafeDictionary<string, GasBlueprint>(modDataStore.AtmosphericGas);
            TechCategories = new SafeDictionary<string, TechCategoryBlueprint>(modDataStore.TechCategories);
            SystemGenSettings = modDataStore.SystemGenSettings["default-system-gen-settings"];

            // Minerals/materials receive EntityIDCounter IDs during mod load.
            // ClearGlobalState() resets that counter to 0 while those objects keep
            // their old IDs — new ComponentDesigns would then collide in CargoGoods
            // and overwrite minerals (Quickstart: Sequence contains no elements).
            EnsureCargoableIdCounter(modDataStore);
        }

        /// <summary>
        /// Keep <see cref="EntityIDCounter"/> ahead of every mineral/material ID
        /// already minted while loading mods.
        /// </summary>
        private static void EnsureCargoableIdCounter(ModDataStore modDataStore)
        {
            int next = EntityIDCounter;
            foreach (var mineral in modDataStore.Minerals.Values)
            {
                if (mineral.ID >= next)
                    next = mineral.ID + 1;
            }
            foreach (var material in modDataStore.ProcessedMaterials.Values)
            {
                if (material.ID >= next)
                    next = material.ID + 1;
            }
            EntityIDCounter = next;
        }

        [CanBeNull]
        public Player? GetPlayerForToken(AuthenticationToken authToken)
        {
            if (SpaceMaster.IsTokenValid(authToken))
            {
                return SpaceMaster;
            }

            Player? foundPlayer = Players.Find(player => player.ID == authToken?.PlayerID);
            return foundPlayer?.IsTokenValid(authToken) != null ? foundPlayer : null;
        }

        public GasBlueprint GetGasBySymbol(string symbol)
        {
            return AtmosphericGases.Values.Where(g => g.ChemicalSymbol.Equals(symbol)).First();
        }

        public static int GetEntityID() => EntityIDCounter++;

        /// <summary>
        /// Allocates and returns the next available faction mask index.
        /// </summary>
        /// <returns>A unique index (0-31) for use in faction bit masks.</returns>
        /// <exception cref="InvalidOperationException">Thrown if all 32 faction slots are used.</exception>
        internal int AllocateFactionMaskIndex()
        {
            if (NextFactionMaskIndex >= MaxFactions)
                throw new InvalidOperationException($"Cannot create more than {MaxFactions} factions.");
            return NextFactionMaskIndex++;
        }

        public static string Save(Game game)
        {
            JsonSerializerSettings settings = new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                TypeNameHandling = TypeNameHandling.Objects,
                ContractResolver = new NonPublicResolver()
            };

            return JsonConvert.SerializeObject(game, settings);
        }

        public static Game Load(string json)
        {
            JsonSerializerSettings settings = new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                TypeNameHandling = TypeNameHandling.Objects,
                ContractResolver = new NonPublicResolver(),
            };

            // Reset statics before deserialize so EventManager/etc. are clean; ID counters are
            // restored from the JSON via NextEntityID / EntityIDCounterValue setters.
            ClearGlobalState();
            var loadedGame = JsonConvert.DeserializeObject<Game>(json, settings)
                ?? throw new JsonSerializationException("Failed to deserialize game save.");
            // Safety net: even old saves / partial counter writes must not collide with live IDs.
            loadedGame.ResyncEntityIdGenerator();

            loadedGame.TimePulse.Initialize(loadedGame);
            loadedGame.ProcessorManager = new ProcessorManager(loadedGame);
            loadedGame.OrderHandler = new StandAloneOrderHandler(loadedGame);
            // postLoad: keep saved system clocks / interrupt queues; only rebind runtime refs.
            loadedGame.GlobalManager.Initialize(loadedGame, postLoad: true);

            foreach (var mgr in loadedGame.GlobalManagerDictionary)
            {
                mgr.Value.Initialize(loadedGame, postLoad: true);
            }

            // Hook up the event logs (TimePulse is [JsonIgnore] on FactionEventLog — rebind it).
            foreach (var (id, faction) in loadedGame.Factions)
            {
                var info = faction.GetDataBlob<FactionInfoDB>();
                if (info.EventLog is FactionEventLog factionLog)
                    factionLog.BindTimePulse(loadedGame.TimePulse);
                info.EventLog.Subscribe();
            }
            var gmInfo = loadedGame.GameMasterFaction.GetDataBlob<FactionInfoDB>();
            if (gmInfo.EventLog is FactionEventLog gmLog)
                gmLog.BindTimePulse(loadedGame.TimePulse);
            gmInfo.EventLog.Subscribe();

            // ActivityState is [JsonIgnore] and defaults to Stasis after deserialize.
            foreach (var system in loadedGame.Systems)
                system.UpdateActivityState();

            // ComponentInstancesDB deserializes instances without re-running OnComponentInstallation;
            // re-sum derived colony totals so save/load cannot leave PointsPerDay (etc.) stale.
            foreach (var system in loadedGame.Systems)
            {
                foreach (var colony in system.GetAllEntitiesWithDataBlob<Colonies.ColonyInfoDB>())
                    ReCalcProcessor.ReCalcAbilities(colony);
            }

            return loadedGame;
        }

        /// <summary>
        /// Advances <see cref="EntityIDGenerator"/> past every entity ID present in this game.
        /// </summary>
        internal void ResyncEntityIdGenerator()
        {
            int maxId = -1;
            foreach (var mgr in GlobalManagerDictionary.Values)
            {
                foreach (var entity in mgr.GetAllEntites())
                {
                    if (entity.Id > maxId)
                        maxId = entity.Id;
                }
            }
            // Also cover the global manager if it is not in the dictionary yet.
            if (GlobalManager != null)
            {
                foreach (var entity in GlobalManager.GetAllEntites())
                {
                    if (entity.Id > maxId)
                        maxId = entity.Id;
                }
            }
            EntityIDGenerator.EnsureMinimum(maxId + 1);
        }

        public void PostNewGameInitialization()
        {
            // Link all JumpPoints between systems
            JPFactory.LinkAllJumpPoints(this);

            // There are few DB's that need to run the processor when the game begins
            foreach (var system in Systems)
            {
                var entitiesWithEnergyGen = system.GetAllEntitiesWithDataBlob<EnergyGenAbilityDB>();
                foreach (var entity in entitiesWithEnergyGen)
                {
                    ProcessorManager.GetInstanceProcessor(nameof(EnergyGenProcessor)).ProcessEntity(entity, TimePulse.GameGlobalDateTime);
                }

                var entitiesWithSensors = system.GetAllEntitiesWithDataBlob<SensorAbilityDB>();
                foreach (var entity in entitiesWithSensors)
                {
                    ProcessorManager.GetInstanceProcessor(nameof(SensorScan)).ProcessEntity(entity, TimePulse.GameGlobalDateTime);
                }

                // Systems with faction entities start as Background, others stay Stasis (default)
                system.UpdateActivityState();
            }
        }
    }

    // public class GameConverter : JsonConverter
    // {
    //     public override bool CanConvert(Type objectType)
    //     {
    //         return objectType == typeof(Game);
    //     }

    //     public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    //     {
    //         var jsonObject = JToken.Load(reader);

    //         List<ModManifest> modManifests = jsonObject["ModInfo"].ToObject<List<ModManifest>>(serializer);

    //         ModLoader modLoader = new ModLoader();
    //         ModDataStore modDataStore = new ModDataStore();

    //         foreach(var manifest in modManifests)
    //         {
    //             var modInfoFilePath = Path.Combine(manifest.ModDirectory, "modInfo.json");

    //             modLoader.LoadModManifest(modInfoFilePath, modDataStore);
    //         }

    //         var settings = jsonObject["Settings"].ToObject<NewGameSettings>(serializer);
    //         //var settings = new NewGameSettings();

    //         var game = jsonObject["GameInfo"].ToObject<Game>(serializer);
    //         game.ApplySettings(settings);
    //         game.ApplyModData(modDataStore);
    //         game.Initialize();

    //         return game;
    //     }

    //     public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    //     {
    //         var game = (Game)value;
    //         var jsonObject = new JObject();

    //         var modInfo = JToken.FromObject(game.StartingGameData.ModManifests, serializer);
    //         var settings = JToken.FromObject(game.Settings, serializer);
    //         var gameInfo = new JObject();

    //         // For each property in the Game class, serialize it using the serializer.
    //         gameInfo["TimePulse"] = JToken.FromObject(game.TimePulse, serializer);

    //         // ProcessManager currently doesn't need to be serialized
    //         //gameInfo["ProcessManager"] = JToken.FromObject(game.ProcessorManager, serializer);

    //         // StandAloneOrderHandler currently doesn't need to be serialized
    //         // gameInfo["OrderHandler"] = JToken.FromObject(game.OrderHandler, serializer);

    //         gameInfo["GlobalManager"] = JToken.FromObject(game.GlobalManager, serializer);
    //         gameInfo["GameMasterFaction"] = JToken.FromObject(game.GameMasterFaction, serializer);
    //         gameInfo["Systems"] = JToken.FromObject(game.Systems, serializer);
    //         // TODO: Serialize the other properties.

    //         jsonObject["ModInfo"] = modInfo;
    //         jsonObject["Settings"] = settings;
    //         jsonObject["GameInfo"] = gameInfo;

    //         jsonObject.WriteTo(writer);
    //     }
    // }
}
