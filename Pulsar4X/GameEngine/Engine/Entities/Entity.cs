using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Pulsar4X.Extensions;
using Pulsar4X.Names;

namespace Pulsar4X.Engine;

[DebuggerDisplay("{DebuggerDisplay}")]
public class Entity : IHasDataBlobs, IEquatable<Entity>
{
    public int Id { get; private set; }

    [JsonIgnore]
    public EntityManager? Manager { get; internal set; }

    /// <summary>Entity manager after <see cref="EntityManager.AddEntity"/>; throws if not attached yet.</summary>
    [JsonIgnore]
    public EntityManager AttachedManager => RequireManager();

    [JsonConstructor]
    private Entity(int id)
    {
        Id = id;
    }

    /// <summary>Used when the ID generator lagged behind a loaded save and minted a collision.</summary>
    internal void ReassignId(int newId) => Id = newId;

    public static Entity Create()
    {
        int entityId = EntityIDGenerator.GenerateUniqueID();
        return new Entity(entityId);
    }

    public static Entity Create(int factionId)
    {
        var entity = Create();
        entity.FactionOwnerID = factionId;
        return entity;
    }

    public static readonly Entity InvalidEntity = new Entity(-1);

    private EntityManager RequireManager([CallerMemberName] string? caller = null)
    {
        if (Manager is null)
            throw new InvalidOperationException($"Entity#{Id} has no Manager ({caller}).");
        return Manager;
    }

    [JsonProperty]
    public bool IsValid { get; internal set; }
    /* Maybe we should do the below, but I'm unsure if IsValid is being checked elswhere for a tag if it's set for removal
    public bool IsValid
    {
        get
        {
            if (Manager == null)
                return false;
            else
                return Manager.IsValidEntity(this);
        }
    }*/

    public T GetRequiredDataBlob<T>() where T : BaseDataBlob
    {
        var manager = RequireManager();
        if (!TryGetDataBlob(out T? blob) || blob is null)
            throw new KeyNotFoundException($"BlobType {typeof(T)} not found on entity#{Id} in manager {manager.ManagerID}.");
        return blob;
    }

    public BaseDataBlob GetRequiredDataBlob(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var manager = RequireManager();
        if (!TryGetDataBlob(type, out object? value) || value is not BaseDataBlob blob)
            throw new KeyNotFoundException($"BlobType {type} not found on entity#{Id} in manager {manager.ManagerID}.");
        return blob;
    }

    public T GetDataBlob<T>() where T : BaseDataBlob
    {
        return RequireManager().GetDataBlob<T>(Id);
    }

    /// <summary>Prefer <see cref="TryGetDataBlob"/> or <see cref="GetRequiredDataBlob"/> when absence is possible.</summary>
    public BaseDataBlob GetDataBlob(Type type)
    {
        return RequireManager().GetDataBlob(Id, type);
    }

    public bool HasDataBlob<T>() where T : BaseDataBlob
    {
        return Manager is not null && Manager.HasDataBlob<T>(Id);
    }

    public bool HasDataBlob(Type type)
    {
        return Manager is not null && Manager.HasDataBlob(Id, type);
    }

    public bool TryGetDataBlob(Type type, [NotNullWhen(true)] out object? value)
    {
        if (Manager is null)
        {
            value = null;
            return false;
        }

        if (Manager.TryGetDataBlob(Id, type, out value))
        {
            return value != null;
        }

        value = null;
        return false;
    }

    public bool TryGetDataBlob<T>([NotNullWhen(true)] out T? value) where T : BaseDataBlob
    {
        if (Manager is null)
        {
            value = null;
            return false;
        }

        if (Manager.TryGetDataBlob<T>(Id, out value))
        {
            return value != null;
        }

        value = null;
        return false;
    }

    public List<BaseDataBlob> GetAllDataBlobs()
    {
        return RequireManager().GetAllDataBlobsForEntity(Id);
    }

    public void SetDataBlob<T>(T dataBlob) where T : BaseDataBlob
    {
        RequireManager().SetDataBlob(Id, dataBlob);
    }

    public void RemoveDataBlob<T>() where T : BaseDataBlob
    {
        RequireManager().RemoveDatablob<T>(Id);
    }

    [JsonIgnore]
    public DateTime StarSysDateTime
    {
        get { return RequireManager().StarSysDateTime; }
    }

    public int FactionOwnerID { get; set; }

    [JsonIgnore]
    public Entity GetFactionOwner => RequireManager().Game.Factions[FactionOwnerID];

    public void AddComponent(ComponentInstance componentInstance)
    {
        if (Manager is null)
            throw new InvalidOperationException($"Cannot AddComponent: entity#{Id} has no Manager (not AddEntity'd yet).");

        componentInstance.ParentEntity = this;
        if (!TryGetDataBlob<ComponentInstancesDB>(out var instancesDB) || instancesDB is null)
            throw new InvalidOperationException($"Cannot AddComponent: entity#{Id} has no ComponentInstancesDB.");

        instancesDB.AddComponentInstance(componentInstance);

        foreach (var atbkvp in componentInstance.Design.AttributesByType)
        {
            atbkvp.Value.OnComponentInstallation(this, componentInstance);
        }

        ReCalcProcessor.ReCalcAbilities(this);
    }

    public void AddComponent(ComponentDesign componentDesign, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            AddComponent(new ComponentInstance(componentDesign));
        }
    }

    public void AddComponent(List<ComponentInstance> instances)
    {
        foreach (var instance in instances)
        {
            AddComponent(instance);
        }
    }

    public void AddComponent(List<ComponentDesign> designs)
    {
        foreach (var design in designs)
        {
            AddComponent(design);
        }
    }

    public void RemoveComponent(ComponentInstance instance)
    {
        instance.ParentEntity = this;
        var instancesDB = GetDataBlob<ComponentInstancesDB>();

        foreach (var atbkvp in instance.Design.AttributesByType)
        {
            atbkvp.Value.OnComponentUninstallation(this, instance);
        }

        instancesDB.RemoveComponentInstance(instance);
        ReCalcProcessor.ReCalcAbilities(this);
    }

    public void Destroy()
    {
        RequireManager().TagEntityForRemoval(this);
        //manager does this:
        //Manager = null;
        //FactionOwnerID = -1;
    }

    public bool Equals(Entity? other)
    {
        if (other is null)
            return false;
        if (Manager is null || other.Manager is null)
            return Id == other.Id && FactionOwnerID == other.FactionOwnerID;

        return Id == other.Id
            && FactionOwnerID == other.FactionOwnerID
            && Manager.ManagerID.Equals(other.Manager.ManagerID);
    }

    [JsonIgnore]
    public string DebuggerDisplay
    {
        get
        {
            var v = $"({Id})";
            if (HasDataBlob<NameDB>())
                v += " " + GetDataBlob<NameDB>().OwnersName;
            return v;
        }
    }
}
