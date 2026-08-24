namespace Pulsar4X.Api;

/// <summary>A snapshot of the simulation clock.</summary>
/// <param name="GameDateTime">Current global simulation time.</param>
/// <param name="IsRunning">Whether the clock is advancing.</param>
/// <param name="IsStopping">Whether the clock is running but has a pending pause/stop request.</param>
/// <param name="TickLength">How much simulation time advances per tick/step (e.g. 1 month).</param>
/// <param name="TickFrequency">Real-time interval between ticks while running (also sets the speed).</param>
/// <param name="IsProcessingTick">True while the engine is calculating the current TickLength increment.</param>
/// <param name="TickProcessStartedUtc">Wall-clock UTC when the current increment calculation started.</param>
/// <param name="LastProcessingTime">How long the previous increment calculation took (estimate for the bar).</param>
/// <param name="TickProgress">0–1 progress through the current TickLength by game-date (interrupt cadence).</param>
public sealed record TimeState(
    DateTime GameDateTime,
    bool IsRunning,
    bool IsStopping,
    TimeSpan TickLength,
    TimeSpan TickFrequency,
    bool IsProcessingTick = false,
    DateTime TickProcessStartedUtc = default,
    TimeSpan LastProcessingTime = default,
    double TickProgress = 0.0);

public enum TimeControlAction
{
    Pause,
    Start,
    StepOnce,
    SetTickLength,
    SetTickFrequency,
}

/// <summary>A request to change the simulation clock (server/SM authority). Only the field relevant to
/// <see cref="Action"/> is read.</summary>
public sealed record TimeControlRequest(
    TimeControlAction Action,
    TimeSpan? StepLength = null,
    TimeSpan? TickLength = null,
    TimeSpan? TickFrequency = null);
