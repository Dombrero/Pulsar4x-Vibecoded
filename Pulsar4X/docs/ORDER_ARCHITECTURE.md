# Order / command architecture (Dombrero fork)

## Priority (unchanged)

1. **Issue** (`OrderSource.Issued`) — player/API commands via `CommandTranslator` → `OrderEnqueue.Issued`
2. **Goals** — `GoalsDB` + `AgentProcessor`; active top-level fleet goal blocks Standing like Issue
3. **Standing** — `FleetOrderProcessor` + list priority + `ActiveStandingOrderIndex` commitment

## Single door into execution

- **`EngineCommandInbox`** — thread-safe queue drained on:
  - end of `EngineGameServer.SubmitCommand`
  - each sub-pulse in `MasterTimePulse.SimulateTimeUntil`
  - `InProcessAdapter.Update` (paused UI pump)
- **`OrderEnqueue`** — only supported way to queue `HandleOrder` / agent wake from new code
- **`Execute`** on `EntityCommand` — movement/math only; no ownership checks (translator meta only)

## Player vs AI

- Player Move/Geo/etc. → `Dispatch` / `OrderEnqueue.Issued` (not `AssignGoal`)
- AI / tests → `AgentProcessor.AssignGoal` + inbox wake

## Future (network)

Replace synchronous drain at submit with engine-thread-only drain; UI pushes DTOs to inbox only.
