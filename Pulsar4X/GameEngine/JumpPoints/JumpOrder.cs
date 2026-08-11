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

    // Non-blocking so early arrivals can run fleet Refuel/Warp while stragglers still transit.
    public override bool IsBlocking => false;

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
        if (gateEntity.AttachedManager != _entityCommanding.AttachedManager)
        {
            DebugTraceLog.Warn("Jump",
                $"fleet#{_entityCommanding.Id}: JumpOrder gate#{gateEntity.Id} is in another system " +
                $"({gateEntity.AttachedManager?.ManagerID}) — abort (would warp to foreign coords)",
                atDateTime);
            _isFinished = true;
            return;
        }

        // Snapshot home tank site before we leave (so remote Refuel can reverse the route).
        RefuelColonySearch.RememberLocalColonyAsRefuelSite(
            _entityCommanding, fleetDB, RequestingFactionGuid);

        // Only hulls already in this system — remote siblings jump/move on their own.
        var ships = fleetDB.Children
            .Where(c => c.HasDataBlob<ShipInfoDB>()
                        && c.AttachedManager == gateEntity.AttachedManager)
            .ToList();

        IsRunning = true;

        // Drop lingering survey warps so ships actually go to the gate.
        foreach (var ship in ships)
            FleetOrderCleanup.AbortShipMovementOrdersOnEntity(ship);

        // Share remaining tank fuel so empty siblings can afford the gate hop.
        double gateDistHint = 0;
        if (gateEntity.TryGetDataBlob<PositionDB>(out var gatePosShare))
        {
            foreach (var ship in ships)
            {
                if (!ship.TryGetDataBlob<PositionDB>(out var sp))
                    continue;
                double d = (gatePosShare.AbsolutePosition - sp.AbsolutePosition).Length();
                if (d > gateDistHint)
                    gateDistHint = d;
            }

            int shared = FleetFuel.TryRedistributeFuelForWarpHop(ships, gateDistHint);
            if (shared > 0)
            {
                DebugTraceLog.Info("Jump",
                    $"fleet#{_entityCommanding.Id}: ship-to-ship fuel share for gate hop ({shared} transfer(s); requires transfer module + range)",
                    atDateTime);
            }
        }

        foreach (var ship in ships)
        {
            // Queue a warp command if the ship isn't already at the gate
            var shipParent = ship.GetDataBlob<PositionDB>().Parent;
            if (shipParent != gateEntity)
            {
                if (!ship.HasDataBlob<WarpAbilityDB>())
                    continue;

                double distToGate = 0;
                if (ship.TryGetDataBlob<PositionDB>(out var shipPos)
                    && gateEntity.TryGetDataBlob<PositionDB>(out var gatePos))
                    distToGate = (gatePos.AbsolutePosition - shipPos.AbsolutePosition).Length();

                if (distToGate > 1e6 && !WarpMoveProcessor.CanAffordWarpHop(ship, distToGate))
                {
                    DebugTraceLog.Warn("Jump",
                        $"ship#{ship.Id}: skip warp to gate#{gateEntity.Id} — not enough tank fuel (stranded)",
                        atDateTime);
                    continue;
                }

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

        // Each hull finishes its own ShipJumpCommand — no flagship gate / mass teleport.
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

        // Move the fleet shell only. Ships transit themselves via ShipJumpCommand —
        // never teleport stragglers that are still warping to the gate.
        if (_entityCommanding.AttachedManager != destManager)
        {
            bool anyMemberThere = false;
            if (_entityCommanding.TryGetDataBlob<FleetDB>(out var membersDb))
            {
                anyMemberThere = membersDb.Children.Any(c =>
                    c.HasDataBlob<ShipInfoDB>() && c.AttachedManager == destManager);
            }

            if (anyMemberThere || _shipJumpCommands.Count == 0)
            {
                destManager.Transfer(_entityCommanding);
                if (_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                {
                    RefuelColonySearch.RememberArrivalGate(fleetDB, destinationEntity);
                    FleetFlagshipSync.TryResolveFlagship(_entityCommanding, fleetDB, out _);
                    FleetStandingSystemSync.OnFlagshipSystemChanged(_entityCommanding, fleetDB);
                }
            }
        }
        else if (_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDBHome))
        {
            RefuelColonySearch.RememberArrivalGate(fleetDBHome, destinationEntity);
            FleetFlagshipSync.TryResolveFlagship(_entityCommanding, fleetDBHome, out _);
        }

        foreach (var cmd in _shipJumpCommands.ToList())
        {
            if (cmd.IsFinished()
                || (cmd.EntityCommanding.IsValid
                    && cmd.EntityCommanding.AttachedManager == destManager))
            {
                cmd.ForceComplete();
            }
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

        // Cross-system gate refs (contaminated knowledge) leave ships hovering forever.
        if (gateEntity.AttachedManager != _entityCommanding.AttachedManager)
        {
            DebugTraceLog.Warn("Jump",
                $"ship#{_entityCommanding.Id}: Transit aborted — gate#{gateEntity.Id} not in this system",
                atDateTime);
            ClearMovementState(_entityCommanding);
            _isFinished = true;
            return;
        }

        // Wait for warp-to-gate (or already hovering at the static JP). Never transit from afar.
        if (!IsShipReadyToTransit(_entityCommanding, gateEntity))
            return;

        if (_entityCommanding.AttachedManager.TryGetGlobalEntityById(_jumpGate.DestinationId, out var destinationEntity))
        {
            JumpTransitDiscovery.RegisterOriginGate(_entityCommanding, _jumpGate);
            JumpTransitDiscovery.EnsureDestinationKnown(_entityCommanding, destinationEntity, atDateTime);

            var destinationPositionDB = destinationEntity.GetDataBlob<PositionDB>();

            ClearMovementState(_entityCommanding);

            // Any ship through the gate can pull the fleet shell into the destination system
            // so Refuel finds the home colony while siblings are still warping/jumping.
            var owningFleet = FleetLookup.FindFleetContainingShip(
                _entityCommanding.AttachedManager.Game, _entityCommanding.Id);
            if (owningFleet.IsValid && owningFleet.TryGetDataBlob<FleetDB>(out var leaveDb))
                RefuelColonySearch.RememberLocalColonyAsRefuelSite(
                    owningFleet, leaveDb, _entityCommanding.FactionOwnerID);

            destinationEntity.AttachedManager.Transfer(_entityCommanding);

            var positionDB = _entityCommanding.GetDataBlob<PositionDB>();
            positionDB.AbsolutePosition = destinationPositionDB.AbsolutePosition;
            positionDB.SetParent(destinationEntity);
            positionDB.MoveType = PositionDB.MoveTypes.None;

            if (owningFleet.IsValid && owningFleet.TryGetDataBlob<FleetDB>(out var arriveDb))
            {
                RefuelColonySearch.RememberArrivalGate(arriveDb, destinationEntity);
                TryMoveFleetShellWithShip(owningFleet, arriveDb, destinationEntity);
            }
        }

        _isFinished = true;
        RefreshOwningFleetJumpOrder(atDateTime);
    }

    /// <summary>
    /// Move the fleet entity into the ship's new system without teleporting other hulls.
    /// </summary>
    private static void TryMoveFleetShellWithShip(Entity fleet, FleetDB fleetDB, Entity destinationGate)
    {
        var destManager = destinationGate.AttachedManager;
        if (destManager == null || fleet.AttachedManager == destManager)
            return;

        destManager.Transfer(fleet);
        FleetFlagshipSync.TryResolveFlagship(fleet, fleetDB, out _);
        FleetStandingSystemSync.OnFlagshipSystemChanged(fleet, fleetDB);
    }

    /// <summary>
    /// Ship must be at / hovering on the gate (or parented to it). WarpMove to MoveTypes.None
    /// targets leaves WarpMovingDB with IsAtTarget — that counts as ready.
    /// </summary>
    private static bool IsShipReadyToTransit(Entity ship, Entity gateEntity)
    {
        if (!ship.IsValid || !gateEntity.IsValid)
            return false;
        if (ship.AttachedManager != gateEntity.AttachedManager)
            return false;

        if (ship.TryGetDataBlob<PositionDB>(out var shipPos) && shipPos.Parent == gateEntity)
            return true;

        if (ship.TryGetDataBlob<WarpMovingDB>(out var warp)
            && warp.IsAtTarget
            && warp.TargetEntity is { IsValid: true } target
            && target.Id == gateEntity.Id)
            return true;

        // Hover / Arrive: AbsolutePosition match (FinishWarp leaves ships parented to the star).
        if (ship.TryGetDataBlob<PositionDB>(out shipPos)
            && gateEntity.TryGetDataBlob<PositionDB>(out var gatePos))
        {
            double dist = (shipPos.AbsolutePosition - gatePos.AbsolutePosition).Length();
            if (dist <= 250_000)
                return true;
            // After a finished warp hop toward this gate, allow a slightly looser window so
            // float error / relative-pose bugs do not leave fleets hovering forever.
            if (ship.TryGetDataBlob<WarpMovingDB>(out warp)
                && warp.IsAtTarget
                && warp.TargetEntity is { IsValid: true } t
                && t.Id == gateEntity.Id
                && dist <= 1_495_978_707.0) // 0.01 AU
                return true;
        }

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

    /// <summary>Drop Transit leftovers when the fleet jump closes (ship already through or done).</summary>
    internal void ForceComplete()
    {
        _isFinished = true;
        if (_entityCommanding.IsValid)
        {
            ClearMovementState(_entityCommanding);
            if (_entityCommanding.TryGetDataBlob<OrderableDB>(out var orders))
            {
                orders.ActionList.Remove(this);
            }
        }
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
