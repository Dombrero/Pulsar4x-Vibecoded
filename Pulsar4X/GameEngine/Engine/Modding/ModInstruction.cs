using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pulsar4X.Blueprints;
using Pulsar4X.Damage;
using Pulsar4X.Industry;

namespace Pulsar4X.Modding
{
    public class ModInstruction
    {
        public enum DataType
        {
            Armor,
            CargoType,
            ComponentTemplate,
            Gas,
            IndustryType,
            Mineral,
            ProcessedMaterial,
            SystemGenSettings,
            Tech,
            TechCategory,
            Theme,
            DamageResistance,
            PartMat,
            Species,
            System,
            Star,
            SystemBody,
            Colony,
            ComponentDesign,
            ShipDesign,
        }
        public enum OperationType { Default, Remove }
        public enum CollectionOperationType { Add, Remove, Overwrite }
        public DataType Type { get; set; }
        public OperationType Operation { get; set; } = OperationType.Default;
        public CollectionOperationType? CollectionOperation { get; set; }

        [JsonIgnore]
        public Blueprint? Data { get; set; }

        public JObject? Payload { get; set; }
    }

    public class ModInstructionJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type? objectType)
        {
            return objectType == typeof(ModInstruction);
        }

        private static T DeserializePayload<T>(JToken payload) where T : Blueprint =>
            payload.ToObject<T>()!;

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            JObject jObject = JObject.Load(reader);
            var instruction = new ModInstruction
            {
                Type = jObject["Type"]!.ToObject<ModInstruction.DataType>()!
            };
            if (jObject["Operation"] != null)
            {
                instruction.Operation = jObject["Operation"]!.ToObject<ModInstruction.OperationType>()!;
            }
            if (jObject["CollectionOperation"] != null)
            {
                instruction.CollectionOperation = jObject["CollectionOperation"]!.ToObject<ModInstruction.CollectionOperationType>();
            }

            instruction.Data = instruction.Type switch
            {
                ModInstruction.DataType.Armor => DeserializePayload<ArmorBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.CargoType => DeserializePayload<CargoTypeBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.ComponentTemplate => DeserializePayload<ComponentTemplateBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.Gas => DeserializePayload<GasBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.IndustryType => DeserializePayload<IndustryTypeBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.Mineral => DeserializePayload<Mineral>(jObject["Payload"]!),
                ModInstruction.DataType.ProcessedMaterial => DeserializePayload<ProcessedMaterial>(jObject["Payload"]!),
                ModInstruction.DataType.SystemGenSettings => jObject["Payload"]!.ToObject<SystemGenSettingsBlueprint>(serializer)!,
                ModInstruction.DataType.Tech => DeserializePayload<TechBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.TechCategory => DeserializePayload<TechCategoryBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.Theme => DeserializePayload<ThemeBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.DamageResistance => DeserializePayload<DamageResistBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.PartMat => DeserializePayload<ParticleMaterialBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.Species => DeserializePayload<SpeciesBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.System => DeserializePayload<SystemBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.Star => DeserializePayload<StarBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.SystemBody => DeserializePayload<SystemBodyBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.Colony => DeserializePayload<ColonyBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.ComponentDesign => DeserializePayload<ComponentDesignBlueprint>(jObject["Payload"]!),
                ModInstruction.DataType.ShipDesign => DeserializePayload<ShipDesignBlueprint>(jObject["Payload"]!),
                _ => throw new JsonSerializationException($"Unknown mod instruction type '{instruction.Type}'."),
            };

            return instruction;
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            ModInstruction modInstruction = (ModInstruction)value;

            JObject jObject = new JObject
            {
                { "Type", modInstruction.Type.ToString() },
                { "Payload", JObject.FromObject(modInstruction.Data) }
            };

            jObject.WriteTo(writer);
        }
    }

}
