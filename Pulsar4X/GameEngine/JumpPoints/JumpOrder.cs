using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.Messaging;
using Pulsar4X.Movement;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;

namespace Pulsar4X.JumpPoints;

/// <summary>
/// Fleet-level jump order. Creates per-ship warp + jump commands so each ship
/// independently warps to the gate and jumps through when it arrives.
/// </summary>
public class JumpOrder : EntityCommand
{
    public override ActionLaneTypes ActionLanes => ActionLaneTypes.Movement;

    public override bool IsBlocking => true;

    public override string Name { get; } = "Jump fleet through gate";

    public override string Details { get; } = "Warp fleet to jump gate and transit";

    Entity _factionEntity = Entity.InvalidEntity;
    Entity _entityCommanding = Entity.InvalidEntity;

    public JumpPointDB? JumpGate { get; set; }
    internal override Entity EntityCommanding { get { return _entityCommanding; } }

    List<ShipJumpCommand> _shipJumpCommands = new List<ShipJumpCommand>();

    public static bool CreateAndExecute(Game game, Entity faction, Entity fleetEntity, JumpPointDB jumpGate)
    {
        var cmd = new JumpOrder()
        {
            RequestingFactionGuid = faction.Id,
            EntityCommandingGuid = fleetEntity.Id,
            CreatedDate = fleetEntity.AttachedManager.ManagerSubpulses.StarSysDateTime,
            JumpGate = jumpGate
        };

        return OrderEnqueue.Enqueue(game, cmd);
    }

    internal override void Execute(DateTime atDateTime)
    {
        if (IsRunning) return;

        // Follow-ups inserted by RefuelAction skip OrderEnqueue/IsValidCommand — rebind here.
        if (!_entityCommanding.IsValid)
        {
            var game = JumpGate?.OwningEntity.AttachedManager?.Game;
            if (game == null || !IsValidCommand(game))
            {
                DebugTraceLog.Warn("Jump",
                    $"fleet#{EntityCommandingGuid}: JumpOrder unbound / invalid — cannot start transit",
                    atDateTime);
                _isFinished = true;
                return;
            }
        }

        if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB)) return;
        if (JumpGate == null || !JumpGate.OwningEntity.IsValid)
        {
            DebugTraceLog.Warn("Jump",
                $"fleet#{_entityCommanding.Id}: JumpOrder has no valid jump gate",
                atDateTime);
            _isFinished = true;
            return;
        }

        var gateEntity = JumpGate.OwningEntity;
        var ships = fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()).ToList();

        IsRunning = true;

        // Drop lingering survey warps so ships actually go to the gate.
        FleetOrderCleanup.AbortShipMovementOrders(_entityCommanding);

        foreach (var ship in ships)
        {
            // Queue a warp command if the ship isn't already at the gate
            var shipParent = ship.GetDataBlob<PositionDB>().Parent;
            if (shipParent != gateEntity)
            {
                if (!ship.HasDataBlob<WarpAbilityDB>())
                    continue;

                var warpCmd = Movement.WarpMoveCommand.CreateCommandEZ(ship, gateEntity, atDateTime);
                warpCmd.Source = Source;
                OrderEnqueue.Enqueue(ship.AttachedManager.Game, warpCmd);
            }

            // Queue a per-ship jump command (will execute after warp completes)
            var jumpCmd = ShipJumpCommand.Create(ship, JumpGate);
            jumpCmd.Source = Source;
            OrderEnqueue.Enqueue(ship.AttachedManager.Game, jumpCmd);
            _shipJumpCommands.Add(jumpCmd);
        }

        if (_shipJumpCommands.Count == 0)
        {
            DebugTraceLog.Warn("Jump",
                $"fleet#{_entityCommanding.Id}: jump issued but no ship received a transit command (no warp / not at gate)",
                atDateTime);
            _isFinished = true;
        }
        else
        {
            DebugTraceLog.Info("Jump",
                $"fleet#{_entityCommanding.Id}: warping {_shipJumpCommands.Count} ship(s) to gate#{gateEntity.Id} then transit",
                atDateTime);
        }
    }

    internal override bool IsFinished()
    {
        if (_isFinished)
            return true;

        if (!IsRunning)
            return false;

        if (!AllShipJumpWorkComplete())
            return false;

        CompleteFleetJump();
        return _isFinished;
    }

    private bool AllShipJumpWorkComplete()
    {
        if (!TryGetDestinationGate(out var destinationEntity))
            return _shipJumpCommands.All(c => c.IsFinished());

        var destManager = destinationEntity.AttachedManager;

        foreach (var cmd in _shipJumpCommands)
        {
            if (cmd.IsFinished())
                continue;

            if (cmd.EntityCommanding.IsValid && cmd.EntityCommanding.AttachedManager == destManager)
                continue;

            return false;
        }

        return true;
    }

    private void CompleteFleetJump()
    {
        if (_isFinished)
            return;

        if (!TryGetDestinationGate(out var destinationEntity))
        {
            _isFinished = true;
            return;
        }

        if (JumpGate != null)
            JumpTransitDiscovery.RegisterOriginGate(_entityCommanding, JumpGate);
        JumpTransitDiscovery.EnsureDestinationKnown(_entityCommanding, destinationEntity, _entityCommanding.StarSysDateTime);

        var destManager = destinationEntity.AttachedManager;
        var destPos = destinationEntity.GetDataBlob<PositionDB>();

        if (_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
        {
            foreach (var ship in fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()).ToList())
            {
                if (ship.AttachedManager == destManager)
                    continue;

                ShipJumpCommand.ClearMovementState(ship);
                destManager.Transfer(ship);
                var positionDB = ship.GetDataBlob<PositionDB>();
                positionDB.AbsolutePosition = destPos.AbsolutePosition;
                positionDB.SetParent(destinationEntity);
                positionDB.MoveType = PositionDB.MoveTypes.None;
            }
        }

        if (_entityCommanding.AttachedManager != destManager)
        {
            destManager.Transfer(_entityCommanding);
            if (_entityCommanding.TryGetDataBlob<FleetDB>(out fleetDB))
            {
                FleetFlagshipSync.TryResolveFlagship(_entityCommanding, fleetDB, out _);
                FleetStandingSystemSync.OnFlagshipSystemChanged(_entityCommanding, fleetDB);
            }
        }
        else if (_entityCommanding.TryGetDataBlob<FleetDB>(out fleetDB))
        {
            FleetFlagshipSync.TryResolveFlagship(_entityCommanding, fleetDB, out _);
        }

        var game = _entityCommanding.AttachedManager?.Game;
        if (game != null && game.Factions.TryGetValue(_entityCommanding.FactionOwnerID, out var factionEntity))
            FleetHierarchy.EnsureFleetRegistered(factionEntity, _entityCommanding);

        DebugTraceLog.Info("Jump",
            $"fleet#{_entityCommanding.Id}: jump complete → system {destManager.ManagerID}",
            _entityCommanding.StarSysDateTime);

        PublishOrdersChanged(_entityCommanding);
        _ = MessagePublisher.Instance.Publish(Message.Create(
            MessageTypes.FleetReorganized,
            factionId: _entityCommanding.FactionOwnerID));
        _isFinished = true;
    }

    private bool TryGetDestinationGate(out Entity destinationEntity)
    {
        destinationEntity = Entity.InvalidEntity;
        if (JumpGate == null || !JumpGate.OwningEntity.IsValid)
            return false;

        return JumpGate.OwningEntity.AttachedManager.TryGetGlobalEntityById(JumpGate.DestinationId, out destinationEntity);
    }

    private static void PublishOrdersChanged(Entity fleet)
    {
        _ = MessagePublisher.Instance.Publish(Message.Create(
            MessageTypes.OrdersChanged,
            entityId: fleet.Id,
            systemId: fleet.AttachedManager.ManagerID,
            factionId: fleet.FactionOwnerID));
    }

    internal override bool IsValidCommand(Game game)
    {
        if (CommandHelpers.IsCommandValid(game.GlobalManager, RequestingFactionGuid, EntityCommandingGuid, out _factionEntity, out _entityCommanding))
        {
            return true;
        }
        return false;
    }

    internal override void BindCommandingEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _entityCommanding = entity;
        base.BindCommandingEntity(entity);
    }

    public override EntityCommand Clone()
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Per-ship jump command. Transfers a single ship through a jump gate
/// when executed (typically after a warp command completes).
/// </summary>
public class ShipJumpCommand : EntityCommand
{
    public override ActionLaneTypes ActionLanes => ActionLaneTypes.Movement;

    public override bool IsBlocking => false;

    public override string Name { get; } = "Transit jump gate";

    public override string Details { get; } = "Transit through the jump gate";

    Entity _factionEntity = Entity.InvalidEntity;
    Entity _entityCommanding = Entity.InvalidEntity;
    JumpPointDB _jumpGate;

    internal override Entity EntityCommanding => _entityCommanding;

    public static ShipJumpCommand Create(Entity ship, JumpPointDB jumpGate)
    {
        return new ShipJumpCommand()
        {
            RequestingFactionGuid = ship.FactionOwnerID,
            EntityCommandingGuid = ship.Id,
            CreatedDate = ship.AttachedManager.ManagerSubpulses.StarSysDateTime,
            _jumpGate = jumpGate,
        };
    }

    internal override void Execute(DateTime atDateTime)
    {
        if (!_jumpGate.OwningEntity.IsValid) { _isFinished = true; return; }

        if (!_entityCommanding.IsValid)
        {
            var game = _jumpGate.OwningEntity.AttachedManager?.Game;
            if (game == null || !IsValidCommand(game))
            {
                _isFinished = true;
                return;
            }
        }

        var gateEntity = _jumpGate.OwningEntity;

        // Wait for warp-to-gate (or already hovering at the static JP). Never transit from afar.
        if (!IsShipReadyToTransit(_entityCommanding, gateEntity))
            return;

        if (_entityCommanding.AttachedManager.TryGetGlobalEntityById(_jumpGate.DestinationId, out var destinationEntity))
        {
            JumpTransitDiscovery.RegisterOriginGate(_entityCommanding, _jumpGate);
            JumpTransitDiscovery.EnsureDestinationKnown(_entityCommanding, destinationEntity, atDateTime);

            var destinationPositionDB = destinationEntity.GetDataBlob<PositionDB>();

            ClearMovementState(_entityCommanding);

            destinationEntity.AttachedManager.Transfer(_entityCommanding);

            var positionDB = _entityCommanding.GetDataBlob<PositionDB>();
            positionDB.AbsolutePosition = destinationPositionDB.AbsolutePosition;
            positionDB.SetParent(destinationEntity);
            positionDB.MoveType = PositionDB.MoveTypes.None;
        }

        _isFinished = true;
        RefreshOwningFleetJumpOrder(atDateTime);
    }

    /// <summary>
    /// Ship must be at / hovering on the gate (or parented to it). WarpMove to MoveTypes.None
    /// targets leaves WarpMovingDB with IsAtTarget — that counts as ready.
    /// </summary>
    private static bool IsShipReadyToTransit(Entity ship, Entity gateEntity)
    {
        if (!ship.IsValid || !gateEntity.IsValid)
            return false;

        if (ship.TryGetDataBlob<PositionDB>(out var shipPos) && shipPos.Parent == gateEntity)
            return true;

        if (ship.TryGetDataBlob<WarpMovingDB>(out var warp)
            && warp.IsAtTarget
            && warp.TargetEntity is { IsValid: true } target
            && target.Id == gateEntity.Id)
            return true;

        if (ship.TryGetDataBlob<PositionDB>(out shipPos)
            && gateEntity.TryGetDataBlob<PositionDB>(out var gatePos)
            && shipPos.GetDistanceTo_m(gatePos) <= 250_000)
            return true;

        return false;
    }

    internal static void ClearMovementState(Entity ship)
    {
        if (ship.HasDataBlob<OrbitDB>())
            ship.RemoveDataBlob<OrbitDB>();
        if (ship.HasDataBlob<OrbitUpdateOftenDB>())
            ship.RemoveDataBlob<OrbitUpdateOftenDB>();
        if (ship.HasDataBlob<WarpMovingDB>())
            ship.RemoveDataBlob<WarpMovingDB>();
        if (ship.HasDataBlob<NewtonMoveDB>())
            ship.RemoveDataBlob<NewtonMoveDB>();
        if (ship.HasDataBlob<NewtonSimpleMoveDB>())
            ship.RemoveDataBlob<NewtonSimpleMoveDB>();
    }

    /// <summary>
    /// Fleet jump completion is driven by per-ship transits; poke the fleet processor so
    /// the issued Jump order is removed as soon as the last ship arrives (not only on the
    /// next fleet hotloop tick in the old system).
    /// </summary>
    private void RefreshOwningFleetJumpOrder(DateTime atDateTime)
    {
        var game = _entityCommanding.AttachedManager?.Game;
        if (game == null)
            return;

        var fleet = FleetLookup.FindFleetContainingShip(game, _entityCommanding.Id);
        if (!fleet.IsValid || !fleet.TryGetDataBlob<OrderableDB>(out _))
            return;

        try
        {
            game.ProcessorManager
                .GetInstanceProcessor(nameof(OrderableProcessor))
                .ProcessEntity(fleet, atDateTime);
        }
        catch
        {
            // Fleet cleanup will run on the next OrderableProcessor pass.
        }
    }

    internal override bool IsFinished()
    {
        return _isFinished;
    }

    internal override bool IsValidCommand(Game game)
    {
        if (CommandHelpers.IsCommandValid(game.GlobalManager, RequestingFactionGuid, EntityCommandingGuid, out _factionEntity, out _entityCommanding))
        {
            return true;
        }
        return false;
    }

    public override EntityCommand Clone()
    {
        throw new NotImplementedException();
    }
}
