# Order / command architecture (Dombrero fork)

## Priority (unchanged)

1. **Issue** (`OrderSource.Issued`) — player/API commands via `CommandTranslator` → `OrderEnqueue.Issued`
2. **Goals** — `GoalsDB` + `AgentProcessor`; active top-level fleet goal blocks Standing like Issue
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

## Player vs AI

- Player Move/Geo/etc. → `Dispatch` / `OrderEnqueue.Issued` (not `AssignGoal`)
- AI / tests → `AgentProcessor.AssignGoal` + inbox wake
- Shared pause API: `FleetOrderCleanup.PauseStandingForPlayerIssue` (Issue + top-level fleet goals)

## Future (network)

Optional: move drain fully off the submit path onto an async network thread; UI pushes DTOs to inbox only. The continuous engine pump already drains while paused; inbox drain itself can stay synchronous inside that pump.
