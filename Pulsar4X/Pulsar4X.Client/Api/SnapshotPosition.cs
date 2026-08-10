using System;
using Pulsar4X.Api;
using Pulsar4X.Orbital;

namespace Pulsar4X.Client;

/// <summary>
/// An <see cref="IPosition"/> backed by the replicated galaxy: every read resolves the entity's
/// current snapshot at the global tick clock (<see cref="GlobalUIState.SimTimeForSystem"/>).
/// Keplerian movers propagate analytically to that clock; warp and other non-Kepler movers use the
/// last pushed <see cref="PositionView"/>. Display updates once per Ticklength (Aurora increments).
/// </summary>
/// <summary>A fixed <see cref="IPosition"/>, for icons placed at synthetic coordinates (galaxy map).</summary>
public sealed class StaticPosition : IPosition
{
    public StaticPosition(Vector3 position)
    {
        AbsolutePosition = position;
        RelativePosition = position;
    }

    public Vector3 AbsolutePosition { get; }
    public Vector3 RelativePosition { get; }
}

public class SnapshotPosition : IPosition
{
    private readonly GlobalUIState _state;
    private readonly string _systemId;
    private readonly int _entityId;

    // Memo of the last computed result, keyed by (snapshot reference, clock, warp flag).
    private EntitySnapshot? _memoSnapshot;
    private DateTime _memoTime;
    private bool _memoWasWarping;
    private (Vector3 absolute, Vector3 relative) _memo;

    public SnapshotPosition(GlobalUIState state, string systemId, int entityId)
    {
        _state = state;
        _systemId = systemId;
        _entityId = entityId;
    }

    public Vector3 AbsolutePosition => Current().absolute;

    public Vector3 RelativePosition => Current().relative;

    private (Vector3 absolute, Vector3 relative) Current()
    {
        var galaxy = _state.GameClient?.Galaxy;
        var system = galaxy?.GetSystem(_systemId);
        var entity = system?.GetEntity(_entityId);
        if (galaxy == null || system == null || entity == null)
            return _memo;

        DateTime now = _state.SimTimeForSystem(_systemId);
        bool warping = entity.HasView<WarpMovingView>();
        if (ReferenceEquals(entity, _memoSnapshot) && now == _memoTime && warping == _memoWasWarping)
            return _memo;

        Vector3 relative;
        Vector3 absolute;
        if (warping)
        {
            // Authoritative mid-warp position from the last server push — not chord interpolation.
            var position = entity.GetView<PositionView>();
            if (position != null)
            {
                absolute = new Vector3(position.AbsolutePosition.X, position.AbsolutePosition.Y, position.AbsolutePosition.Z);
                relative = new Vector3(position.RelativePosition.X, position.RelativePosition.Y, position.RelativePosition.Z);
            }
            else
            {
                absolute = Vector3.Zero;
                relative = Vector3.Zero;
            }
        }
        else
        {
            var orbit = entity.GetView<OrbitView>();
            if (orbit != null && orbit.StandardGravParameter > 0)
            {
                relative = orbit.RelativePositionM(now);
            }
            else
            {
                var position = entity.GetView<PositionView>();
                relative = position != null
                    ? new Vector3(position.RelativePosition.X, position.RelativePosition.Y, position.RelativePosition.Z)
                    : Vector3.Zero;
            }

            absolute = entity.AbsolutePositionM(system, now);
        }

        _memoSnapshot = entity;
        _memoTime = now;
        _memoWasWarping = warping;
        _memo = (absolute, relative);
        return _memo;
    }
}
