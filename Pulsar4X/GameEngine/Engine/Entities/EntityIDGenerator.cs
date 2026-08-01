using Newtonsoft.Json;

namespace Pulsar4X.Engine;

/// <summary>
/// Monotonic source of entity IDs. Must stay ahead of every ID already present in a loaded game;
/// otherwise <see cref="Entity.Create"/> reissues IDs and <see cref="EntityManager.AddEntity"/> throws.
/// </summary>
public static class EntityIDGenerator
{
    [JsonProperty]
    internal static int NextId { get; set; } = 0;

    public static int GenerateUniqueID() => NextId++;

    public static void Reset(int nextId = 0) => NextId = nextId;

    /// <summary>Ensure the next minted ID is at least <paramref name="minimumNextId"/>.</summary>
    public static void EnsureMinimum(int minimumNextId)
    {
        if (NextId < minimumNextId)
            NextId = minimumNextId;
    }
}
