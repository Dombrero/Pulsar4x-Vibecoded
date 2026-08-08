# Order / command architecture (Dombrero fork)

## Roles (this fork)

- **Standing** — player fleet automation (conditions + list priority + commitment). Stays. Goals do **not** replace Standing.
- **Goals / Agent** — AI and optional player **auto / freewill mode** only (`AssignGoal`). Not the default Issue path.
- **Issue** — normal player clicks / API (`OrderEnqueue.Issued`).

## Priority (unchanged)

1. **Issue** (`OrderSource.Issued`) — player/API commands via `CommandTranslator` → `OrderEnqueue.Issued`
2. **Goals** — `GoalsDB` + `AgentProcessor` (AI / auto-mode); active top-level fleet goal blocks Standing like Issue
3. **Standing** — `FleetOrderProcessor` + list priority + `ActiveStandingOrderIndex` commitment

## Single door into execution

- **`EngineCommandInbox`** — thread-safe queue drained on:
  - continuous ~50ms pump in `EngineGameServer` (works while paused; Discord engine-side inbox loop)
  - end of `EngineGameServer.SubmitCommand`
  - each sub-pulse in `MasterTimePulse.SimulateTimeUntil`
  - `InProcessAdapter.Update` (paused UI pump)
- **`OrderEnqueue`** — only supported way to queue `HandleOrder` / agent wake from new code
  - Standing missions use `OrderEnqueue.Standing` (not raw `ActionList.Add`)
- **`IsValidCommand` / `IsCommandValid`** — entity-alive + resolve refs only; ownership/auth is `CommandTranslator` / `EngineGameServer`
- **`Execute`** on `EntityCommand` — movement/math only; no ownership checks (translator meta only)

## Player vs AI / auto-mode

- Normal player Move/Geo/etc. → `Dispatch` / `OrderEnqueue.Issued` (not `AssignGoal`)
- Player conditional automation → **Standing** (fuel &lt; 30% → refuel, etc.)
- AI or freewill/auto-mode → `AgentProcessor.AssignGoal` + inbox wake
- Shared pause API: `FleetOrderCleanup.PauseStandingForPlayerIssue` (Issue + top-level fleet goals)

## Fleet mission vs ship steps (Option B)

- **Fleet** owns Standing list, ENTER/EXIT, commitment (`ActiveStandingOrderIndex`), preempt.
- **Ships** pick the next *local* step at action boundaries via `ShipStandingDirector` (logistics override, then mission step handlers).
- Same fleet mission for all ships; ships may use different targets (per-ship nearest geo/grav).
- New **conditions** stay on the fleet order. New **mission actions** with parallel hull work register an `IShipStandingStep` (unknown actions fall back to fleet enqueue).
- Standing eval is event-driven (`TryEvaluateNow` / director) with a rare daily fleet safety poll — not a per-ship hotloop.

## Future (network)

Optional: move drain fully off the submit path onto an async network thread; UI pushes DTOs to inbox only. The continuous engine pump already drains while paused; inbox drain itself can stay synchronous inside that pump.
