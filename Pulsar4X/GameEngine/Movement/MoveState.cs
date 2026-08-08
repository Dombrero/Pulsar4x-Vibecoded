using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Galaxy;
using Pulsar4X.Interfaces;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;

namespace Pulsar4X.Movement;


public class PositionDB : TreeHierarchyDB, IPosition
{
    /// <summary>
    /// Most objects should have a movetype. none should be used in rare occasions eg anomalies/jump points.
    /// ships at None type objects should remain warping with a speed of zero, but be using warp resources.
    /// </summary>
    public enum MoveTypes
    {
        None,
        Orbit,
        NewtonSimple,
        NewtonComplex,
        Warp,
    }
    [JsonProperty]
    public MoveTypes MoveType { get; internal set; }

    public KeplerElements GetKeplerElements { get; internal set; }

    public Vector3 RelativePosition { get; internal set; }

    public Vector2 RelativePosition2
    {
        get { return (Vector2)RelativePosition; }
        set { RelativePosition = (Vector3)value; }
    }
    public Vector3 AbsolutePosition
    {
        get
        {
            if (Parent == null || !Parent.IsValid) //migth be better than crashing if parent is suddenly not valid. should be handled before this though.
                return RelativePosition;
            else if (Parent == OwningEntity)
                throw new Exception("Infinite loop triggered");
            else
            {
                PositionDB? parentpos = (PositionDB?)ParentDB;
                // Parent may be set before the parent's PositionDB is available (construction/load).
                if (parentpos is null)
                    return RelativePosition;
                if (parentpos == this)
                    throw new Exception("Infinite loop triggered");
                return parentpos.AbsolutePosition + RelativePosition;
            }
        }
        internal set
        {
            if (Parent == null)
                RelativePosition = value;
            else
            {
                PositionDB? parentpos = (PositionDB?)ParentDB;
                RelativePosition = value - (parentpos?.AbsolutePosition ?? Vector3.Zero);
            }
        }
    }

    public Vector2 AbsolutePosition2
    {
        get { return (Vector2)AbsolutePosition; }
        set { AbsolutePosition = (Vector3)value; }
    }
    [JsonProperty]
    public Vector2 Velocity { get; internal set; }
    [JsonProperty]
    public double SGP { get; internal set; }

    [JsonConstructor]
    private PositionDB() : base(null) { }

    /// <summary>
    /// Initialized
    /// .
    /// </summary>
    /// <param name="x">X value.</param>
    /// <param name="y">Y value.</param>
    /// <param name="z">Z value.</param>
    public PositionDB(double x, double y, double z, Entity? parent = null) : base(parent)
    {
        AbsolutePosition = new Vector3(x, y, z);
        SetParent(parent);
        //SystemGuid = systemGuid;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="relativePos_m"></param>
    /// <param name="systemGuid"></param>
    /// <param name="parent"></param>
    public PositionDB(Vector3 relativePos, Entity? parent = null) : base(parent)
    {
        SetParent(parent);
        RelativePosition = relativePos;

    }

    public PositionDB(Entity? parent = null) : base(parent)
    {
        Vector3? parentPos = (ParentDB as PositionDB)?.AbsolutePosition;
        AbsolutePosition = parentPos ?? Vector3.Zero;
    }

    public PositionDB(PositionDB positionDB)
        : base(positionDB.Parent)
    {
        RelativePosition = positionDB.RelativePosition;

    }

    public override object Clone()
    {
        return new PositionDB(this);
    }

    //[UsedImplicitly]

    /// <summary>
    /// changes the positions relative to
    /// Can be null.
    /// </summary>
    /// <param name="newParent"></param>
    internal override void SetParent(Entity? newParent)
    {
        if (newParent != null && !newParent.HasDataBlob<PositionDB>())
            throw new Exception("newParent must have a PositionDB");
        var oldParent = ParentDB;


        Vector3 currentAbsolute = this.AbsolutePosition;
        Vector3 newRelative;
        if (newParent == null || !newParent.HasDataBlob<MassVolumeDB>())
        {
            newRelative = currentAbsolute;
            SGP = double.PositiveInfinity;
        }
        else
        {
            newRelative = currentAbsolute - newParent.GetDataBlob<PositionDB>().AbsolutePosition;
            var mass = newParent.GetDataBlob<MassVolumeDB>().MassTotal;
            if (OwningEntity is { Manager: not null, IsValid: true } owner)
                mass += owner.GetDataBlob<MassVolumeDB>().MassTotal;
            SGP = GeneralMath.StandardGravitationalParameter(mass);
        }
        base.SetParent(newParent);
        RelativePosition = newRelative;

    }
}

public class MoveStateProcessor : IInstanceProcessor
{
    public void Init(Game game)
    {

    }

    static bool HasAttachedOwner(Entity? entity)
        => entity is { Manager: not null, IsValid: true };

    static PositionDB GetOrCreatePositionDB(Entity entity, Entity? parentForNew)
    {
        if (!HasAttachedOwner(entity))
            throw new InvalidOperationException(
                $"Cannot create PositionDB for Entity#{entity?.Id ?? -1} (no attached manager).");

        if (entity.TryGetDataBlob<PositionDB>(out var stateDB) && stateDB is not null)
            return stateDB;
        var created = new PositionDB(parentForNew);
        entity.SetDataBlob(created);
        return created;
    }

    public static void ProcessForType(List<OrbitDB> orbits, DateTime atDateTime)
    {
        foreach (var orbitDB in orbits)
        {
            if (HasAttachedOwner(orbitDB.OwningEntity))
                ProcessForType(orbitDB, atDateTime);
        }
    }

    public static void ProcessForType(List<OrbitDB> orbits, DateTime atDateTime, double[] preCalculatedTrueAnomalies)
    {
        for (int i = 0; i < orbits.Count; i++)
        {
            if (HasAttachedOwner(orbits[i].OwningEntity))
                ProcessForType(orbits[i], atDateTime, preCalculatedTrueAnomalies[i]);
        }
    }

    public static void ProcessForType(OrbitDB orbitDB, DateTime atDateTime)
    {
        if (!HasAttachedOwner(orbitDB.OwningEntity))
            return;
        PositionDB stateDB = GetOrCreatePositionDB(orbitDB.OwningEntity, orbitDB.Parent);

        stateDB.MoveType = PositionDB.MoveTypes.Orbit;
        // Only update parent if it has changed to avoid expensive SetParent operation
        if (stateDB.Parent != orbitDB.Parent)
            stateDB.SetParent(orbitDB.Parent);
        stateDB.SGP = orbitDB.GravitationalParameter_m3S2;
        stateDB.GetKeplerElements = orbitDB.GetElements();
        stateDB.RelativePosition2 = orbitDB._position; //(Vector2)orbitDB.OwningEntity.GetDataBlob<PositionDB>().RelativePosition;
        orbitDB.OwningEntity.GetRequiredDataBlob<PositionDB>().RelativePosition = (Vector3)orbitDB._position;
        stateDB.Velocity = (Vector2)orbitDB.InstantaneousOrbitalVelocityVector_m(atDateTime);
    }

    public static void ProcessForType(OrbitDB orbitDB, DateTime atDateTime, double preCalculatedTrueAnomaly)
    {
        if (!HasAttachedOwner(orbitDB.OwningEntity))
            return;
        PositionDB stateDB = GetOrCreatePositionDB(orbitDB.OwningEntity, orbitDB.Parent);

        stateDB.MoveType = PositionDB.MoveTypes.Orbit;
        // Only update parent if it has changed to avoid expensive SetParent operation
        if (stateDB.Parent != orbitDB.Parent)
            stateDB.SetParent(orbitDB.Parent);
        stateDB.SGP = orbitDB.GravitationalParameter_m3S2;
        stateDB.GetKeplerElements = orbitDB.GetElements(preCalculatedTrueAnomaly);
        stateDB.RelativePosition2 = orbitDB._position; //(Vector2)orbitDB.OwningEntity.GetDataBlob<PositionDB>().RelativePosition;
        orbitDB.OwningEntity.GetRequiredDataBlob<PositionDB>().RelativePosition = (Vector3)orbitDB._position;
        stateDB.Velocity = (Vector2)orbitDB.InstantaneousOrbitalVelocityVector_m(atDateTime, preCalculatedTrueAnomaly);
    }

    public static void ProcessForType(List<OrbitUpdateOftenDB> orbits, DateTime atDateTime)
    {
        foreach (var orbitDB in orbits)
        {
            if (HasAttachedOwner(orbitDB.OwningEntity))
                ProcessForType(orbitDB, atDateTime);
        }
    }

    public static void ProcessForType(OrbitUpdateOftenDB orbitDB, DateTime atDateTime)
    {
        if (!HasAttachedOwner(orbitDB.OwningEntity))
            return;
        PositionDB stateDB = GetOrCreatePositionDB(orbitDB.OwningEntity, orbitDB.Parent);

        stateDB.MoveType = PositionDB.MoveTypes.Orbit;
        // Only update parent if it has changed to avoid expensive SetParent operation
        if (stateDB.Parent != orbitDB.Parent)
            stateDB.SetParent(orbitDB.Parent);
        stateDB.SGP = orbitDB.GravitationalParameter_m3S2;
        stateDB.GetKeplerElements = orbitDB.GetElements();
        stateDB.RelativePosition2 = orbitDB._position;
        orbitDB.OwningEntity.GetRequiredDataBlob<PositionDB>().RelativePosition = (Vector3)orbitDB._position;
        stateDB.Velocity = (Vector2)orbitDB.InstantaneousOrbitalVelocityVector_m(atDateTime);
    }

    public static void ProcessForType(List<NewtonSimpleMoveDB> moves, DateTime atDateTime)
    {
        foreach (var movedb in moves)
        {
            if (!HasAttachedOwner(movedb.OwningEntity))
                continue;
            PositionDB stateDB = GetOrCreatePositionDB(movedb.OwningEntity, movedb.SOIParent);

            stateDB.MoveType = PositionDB.MoveTypes.NewtonSimple;
            // Only update parent if it has changed to avoid expensive SetParent operation
            if (stateDB.Parent != movedb.SOIParent)
                stateDB.SetParent(movedb.SOIParent);
            var myMass = movedb.OwningEntity.GetRequiredDataBlob<MassVolumeDB>().MassTotal;
            var pMass = movedb.SOIParent.GetRequiredDataBlob<MassVolumeDB>().MassTotal;
            stateDB.SGP = GeneralMath.StandardGravitationalParameter(myMass + pMass);
            var state = OrbitMath.GetStateVectors(movedb.CurrentTrajectory, atDateTime);
            stateDB.RelativePosition = state.position;
            stateDB.Velocity = state.velocity;
            var ke = OrbitMath.KeplerFromPositionAndVelocity(stateDB.SGP, state.position, (Vector3)state.velocity, atDateTime);
            stateDB.GetKeplerElements = ke;
        }
    }
    public static void ProcessForType(NewtonSimpleMoveDB movedb, DateTime atDateTime)
    {
        if (!HasAttachedOwner(movedb.OwningEntity))
            return;
        PositionDB stateDB = GetOrCreatePositionDB(movedb.OwningEntity, movedb.SOIParent);

        stateDB.MoveType = PositionDB.MoveTypes.NewtonSimple;
        // Only update parent if it has changed to avoid expensive SetParent operation
        if (stateDB.Parent != movedb.SOIParent)
            stateDB.SetParent(movedb.SOIParent);
        var myMass = movedb.OwningEntity.GetRequiredDataBlob<MassVolumeDB>().MassTotal;
        var pMass = movedb.SOIParent.GetRequiredDataBlob<MassVolumeDB>().MassTotal;
        stateDB.SGP = GeneralMath.StandardGravitationalParameter(myMass + pMass);
        var state = OrbitMath.GetStateVectors(movedb.CurrentTrajectory, atDateTime);
        stateDB.RelativePosition = state.position;
        stateDB.Velocity = state.velocity;
        var ke = OrbitMath.KeplerFromPositionAndVelocity(stateDB.SGP, state.position, (Vector3)state.velocity, atDateTime);
        stateDB.GetKeplerElements = ke;
    }

    public static void ProcessForType(List<NewtonMoveDB> moves, DateTime atDateTime)
    {
        foreach (var movedb in moves)
        {
            if (HasAttachedOwner(movedb.OwningEntity))
                ProcessForType(movedb, atDateTime);
        }
    }

    public static void ProcessForType(NewtonMoveDB movedb, DateTime atDateTime)
    {
        if (!HasAttachedOwner(movedb.OwningEntity))
            return;
        PositionDB stateDB = GetOrCreatePositionDB(movedb.OwningEntity, movedb.SOIParent);

        stateDB.MoveType = PositionDB.MoveTypes.NewtonComplex;
        // Only update parent if it has changed to avoid expensive SetParent operation
        if (stateDB.Parent != movedb.SOIParent)
            stateDB.SetParent(movedb.SOIParent);
        stateDB.GetKeplerElements = movedb.GetElements();
        stateDB.SGP = stateDB.GetKeplerElements.StandardGravParameter;
        //newtonmove processor still updates positon in the processor.
        //stateDB.RelativePosition = (Vector2)movedb.OwningEntity.GetDataBlob<PositionDB>().RelativePosition;
        stateDB.Velocity = (Vector2)movedb.CurrentVector_ms;
    }

    public static void ProcessForType(List<WarpMovingDB> warps, DateTime atDateTime)
    {
        foreach (var warpdb in warps)
        {
            // Warp completion RemoveDataBlob sets OwningEntity = InvalidEntity (never null).
            // Skipping here avoids ProcessSystem: Entity#-1 has no Manager (SetDataBlob).
            if (HasAttachedOwner(warpdb.OwningEntity))
                ProcessForType(warpdb, atDateTime);
        }
    }

    public static void ProcessForType(WarpMovingDB warpdb, DateTime atDateTime)
    {
        if (!HasAttachedOwner(warpdb.OwningEntity))
            return;
        if (!warpdb.OwningEntity.TryGetDataBlob<PositionDB>(out PositionDB? stateDB) || stateDB is null)
        {
            if (!HasAttachedOwner(warpdb._parentEnitity))
                return;
            stateDB = new PositionDB(warpdb._parentEnitity);
            warpdb.OwningEntity.SetDataBlob(stateDB);
        }

        stateDB.MoveType = PositionDB.MoveTypes.Warp;

        // Only update parent if it has changed to avoid expensive SetParent operation
        if (HasAttachedOwner(warpdb._parentEnitity) && stateDB.Parent != warpdb._parentEnitity)
            stateDB.SetParent(warpdb._parentEnitity);
        stateDB.GetKeplerElements = warpdb.EndpointTargetOrbit;
        stateDB.SGP = stateDB.GetKeplerElements.StandardGravParameter;
        stateDB.RelativePosition2 = warpdb._position;
        stateDB.Velocity = (Vector2)warpdb.CurrentNonNewtonionVectorMS;
        if (HasAttachedOwner(stateDB.OwningEntity))
            stateDB.OwningEntity.GetDataBlob<PositionDB>().RelativePosition = (Vector3)warpdb._position;
    }

    public Type GetParameterType => typeof(PositionDB);
    internal override void ProcessEntity(Entity entity, DateTime atDateTime)
    {

        if (entity.TryGetDataBlob<OrbitDB>(out OrbitDB? odb) && odb is not null)
            ProcessForType(odb, atDateTime);
        else if (entity.TryGetDataBlob<OrbitUpdateOftenDB>(out OrbitUpdateOftenDB? oudb) && oudb is not null)
            ProcessForType(oudb, atDateTime);
        else if (entity.TryGetDataBlob<NewtonMoveDB>(out NewtonMoveDB? mdb) && mdb is not null)
            ProcessForType(mdb, atDateTime);
        else if (entity.TryGetDataBlob<NewtonSimpleMoveDB>(out NewtonSimpleMoveDB? nmdb) && nmdb is not null)
            ProcessForType(nmdb, atDateTime);
        else if (entity.TryGetDataBlob<WarpMovingDB>(out WarpMovingDB? warpdb) && warpdb is not null)
            ProcessForType(warpdb, atDateTime);
    }

    /// <summary>
    /// This allows easy single entity move processing regardless of move type.
    /// this should only be used in rare cases where you need to update a position ouside of the normal move tick.
    /// this WILL update the position, to get the position without updating, use GetFuturePosition() instead.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="toDateTime"></param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    internal static void ProcessEntityMove(Entity entity, DateTime toDateTime)
    {
        var movestate = entity.GetDataBlob<PositionDB>();
        switch (movestate.MoveType)
        {
            case PositionDB.MoveTypes.None:
                {
                    break;
                }
            case PositionDB.MoveTypes.Orbit:
                {
                    OrbitProcessor.ProcessEntity(entity, toDateTime);
                    break;
                }
            case PositionDB.MoveTypes.NewtonSimple:
                {
                    NewtonSimpleProcessor.ProcessEntity(entity, toDateTime);
                    break;
                }
            case PositionDB.MoveTypes.NewtonComplex:
                {
                    NewtonionMovementProcessor.ProcessEntity(entity, toDateTime);
                    break;
                }
            case PositionDB.MoveTypes.Warp:
                {
                    if (entity.HasDataBlob<WarpMovingDB>())
                        WarpMoveProcessor.ProcessEntity(entity, toDateTime);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}