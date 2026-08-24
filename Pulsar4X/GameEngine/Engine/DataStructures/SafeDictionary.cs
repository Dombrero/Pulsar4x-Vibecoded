using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pulsar4X.Engine;
using Pulsar4X.Industry;

namespace Pulsar4X.DataStructures
{

    public interface ISafeDictionary
    {
        object this[int index] { get; set; }
    }

    [JsonConverter(typeof(SafeDictionaryConverter))]
    public class SafeDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IEquatable<SafeDictionary<TKey, TValue>>, ISafeDictionary
        where TKey : notnull
    {
        object ISafeDictionary.this[int index]
        {
            get
            {
                TKey key = (TKey)(object)index;
                if (this.TryGetValue(key, out TValue? value))
                {
                    return value;
                }
                else
                {
                    throw new KeyNotFoundException($"Key {index} not found in the dictionary.");
                }
            }
            set => this[(TKey)(object)index] = (TValue)value;
        }

        private readonly Dictionary<TKey, TValue> _innerDictionary = new Dictionary<TKey, TValue>();
        private readonly object _lock = new object();
        public delegate void DictionaryChangedHandler(TKey key, TValue value);

        public event DictionaryChangedHandler? ItemAdded;
        public event DictionaryChangedHandler? ItemRemoved;
        public event DictionaryChangedHandler? OnChange;
        public int Count
        {
            get
            {
                lock (_lock) return _innerDictionary.Count;
            }
        }

        public Dictionary<TKey, TValue>.KeyCollection Keys
        {
            get
            {
                lock (_lock) return _innerDictionary.Keys;
            }
        }

        public Dictionary<TKey, TValue>.ValueCollection Values
        {
            get
            {
                lock (_lock) return _innerDictionary.Values;
            }
        }

        public SafeDictionary() { }
        public SafeDictionary(IDictionary<TKey, TValue> dictionary)
        {
            foreach (var (key, value) in dictionary)
            {
                _innerDictionary.Add(key, value);
            }
        }

        public SafeDictionary(SafeDictionary<TKey, TValue> dictionary)
        {
            foreach (var (key, value) in dictionary)
            {
                _innerDictionary.Add(key, value);
            }
        }

        public TValue this[TKey key]
        {
            get
            {
                lock (_lock) return _innerDictionary[key];
            }
            set
            {
                lock (_lock)
                {
                    _innerDictionary[key] = value;
                    OnChange?.Invoke(key, value);
                }
            }
        }

        public void Add(TKey key, TValue value)
        {
            lock (_lock)
            {
                _innerDictionary.Add(key, value);
                ItemAdded?.Invoke(key, value);
                OnChange?.Invoke(key, value);
            }
        }

        public bool Remove(TKey key)
        {
            lock (_lock)
            {
                if (_innerDictionary.TryGetValue(key, out TValue? value))
                {
                    _innerDictionary.Remove(key);
                    ItemRemoved?.Invoke(key, value);
                    OnChange?.Invoke(key, value);
                    return true;
                }
            }
            return false;
        }

        public bool ContainsKey(TKey key)
        {
            lock (_lock) return _innerDictionary.ContainsKey(key);
        }

        public void Clear()
        {
            lock (_lock) _innerDictionary.Clear();
        }

        public bool TryGetValue(TKey key, [NotNullWhen(true)] out TValue? value)
        {
            lock (_lock)
            {
                if (_innerDictionary.ContainsKey(key))
                {
                    value = _innerDictionary[key];
                    if (value is null)
                    {
                        throw new InvalidOperationException("Unexpected null value in the dictionary.");
                    }
                    return true;
                }
                value = default(TValue);
                return false;
            }
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            List<KeyValuePair<TKey, TValue>> snapshot;
            lock (_lock)
            {
                snapshot = _innerDictionary.ToList();
            }
            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public bool Equals(SafeDictionary<TKey, TValue>? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            lock (_lock)
            {
                lock (other._lock)
                {
                    if (_innerDictionary.Count != other._innerDictionary.Count)
                    {
                        return false;
                    }

                    foreach (var kvp in _innerDictionary)
                    {
                        if (!other._innerDictionary.TryGetValue(kvp.Key, out var value))
                            return false;

                        if (!EqualityComparer<TValue>.Default.Equals(value, kvp.Value))
                            return false;
                    }

                    return true;
                }
            }
        }

        /// <summary>
        /// Needed for serialization
        /// </summary>
        [JsonIgnore]
        internal Dictionary<TKey, TValue> InnerDictionary
        {
            get
            {
                lock (_lock)
                {
                    return new Dictionary<TKey, TValue>(_innerDictionary);
                }
            }
        }
    }

    public class SafeDictionaryConverter : JsonConverter
    {
        public override bool CanConvert(Type? objectType)
        {
            return objectType != null
                && objectType.IsGenericType
                && objectType.GetGenericTypeDefinition() == typeof(SafeDictionary<,>);
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return Activator.CreateInstance(objectType);

            Type keyType = objectType.GetGenericArguments()[0];
            Type valueType = objectType.GetGenericArguments()[1];

            // PreserveReferencesHandling may emit { "$ref": "…" } — resolve via normal dict path.
            if (reader.TokenType == JsonToken.StartObject)
            {
                var obj = JObject.Load(reader);
                if (obj["$ref"] != null)
                {
                    // Let Json.NET resolve the reference into a Dictionary, then wrap.
                    using var subReader = obj.CreateReader();
                    var referred = serializer.Deserialize(
                        subReader,
                        typeof(Dictionary<,>).MakeGenericType(keyType, valueType)) as IDictionary;
                    return CreateFromDictionary(objectType, keyType, valueType, referred);
                }

                var result = Activator.CreateInstance(objectType)
                    ?? throw new JsonSerializationException($"Could not create {objectType}.");
                var add = objectType.GetMethod("Add", new[] { keyType, valueType })
                    ?? throw new JsonSerializationException($"SafeDictionary missing Add({keyType},{valueType}).");

                foreach (var prop in obj.Properties())
                {
                    // Skip metadata from TypeNameHandling / PreserveReferencesHandling.
                    if (prop.Name.Length > 0 && prop.Name[0] == '$')
                        continue;

                    object key = ConvertKey(prop.Name, keyType);
                    object? val = prop.Value?.ToObject(valueType, serializer);
                    add.Invoke(result, new[] { key, val });
                }

                return result;
            }

            // Legacy / unusual payloads: deserialize as Dictionary then wrap.
            var baseDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
            var innerDict = serializer.Deserialize(reader, baseDictType) as IDictionary;
            return CreateFromDictionary(objectType, keyType, valueType, innerDict);
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
                return;
            }

            // Plain object of entries — avoids nested Dictionary+$type payloads that sometimes
            // saved as empty {} under TypeNameHandling (wiping GeoSurveyStatus on disk).
            writer.WriteStartObject();
            foreach (var entry in (IEnumerable)value)
            {
                var entryType = entry.GetType();
                var key = entryType.GetProperty("Key")!.GetValue(entry);
                var val = entryType.GetProperty("Value")!.GetValue(entry);
                writer.WritePropertyName(KeyToPropertyName(key));
                serializer.Serialize(writer, val);
            }
            writer.WriteEndObject();
        }

        private static object CreateFromDictionary(
            Type safeDictType, Type keyType, Type valueType, IDictionary? innerDict)
        {
            var result = Activator.CreateInstance(safeDictType)
                ?? throw new JsonSerializationException($"Could not create {safeDictType}.");
            if (innerDict == null || innerDict.Count == 0)
                return result;

            var add = safeDictType.GetMethod("Add", new[] { keyType, valueType })
                ?? throw new JsonSerializationException($"SafeDictionary missing Add.");
            foreach (DictionaryEntry de in innerDict)
            {
                object key = de.Key is string s
                    ? ConvertKey(s, keyType)
                    : (keyType.IsInstanceOfType(de.Key)
                        ? de.Key
                        : Convert.ChangeType(de.Key, keyType)!);
                add.Invoke(result, new[] { key, de.Value });
            }
            return result;
        }

        private static string KeyToPropertyName(object? key)
        {
            if (key is null)
                return "";
            if (key is Type typeKey)
                return typeKey.AssemblyQualifiedName ?? typeKey.FullName ?? typeKey.Name;
            return Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        private static object ConvertKey(string name, Type keyType)
        {
            if (keyType == typeof(string))
                return name;
            if (keyType == typeof(Type))
            {
                var resolved = Type.GetType(name, throwOnError: false);
                if (resolved != null)
                    return resolved;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    resolved = asm.GetType(name, throwOnError: false);
                    if (resolved != null)
                        return resolved;
                }
                throw new JsonSerializationException($"Could not resolve Type key '{name}'.");
            }
            if (keyType == typeof(Guid))
                return Guid.Parse(name);
            if (keyType.IsEnum)
                return Enum.Parse(keyType, name);
            if (keyType == typeof(int))
                return int.Parse(name, System.Globalization.CultureInfo.InvariantCulture);
            if (keyType == typeof(long))
                return long.Parse(name, System.Globalization.CultureInfo.InvariantCulture);
            if (keyType == typeof(uint))
                return uint.Parse(name, System.Globalization.CultureInfo.InvariantCulture);
            if (typeof(IConvertible).IsAssignableFrom(keyType))
                return Convert.ChangeType(name, keyType, System.Globalization.CultureInfo.InvariantCulture)!;

            throw new JsonSerializationException(
                $"Unsupported SafeDictionary key type {keyType.FullName} for property '{name}'.");
        }
    }

}
