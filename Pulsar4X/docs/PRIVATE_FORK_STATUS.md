# Private fork status (`dombreros_version_AI_vibecoded`)

**Local only — do not push** unless you explicitly choose to (e.g. `mine` remote for backup).

## Done (order / command stack)

- [x] `EngineCommandInbox` — queue + drain (submit, sim sub-pulse, UI pump while paused)
- [x] `OrderEnqueue` — single entry for Issued / FromGoal / Standing / generic Enqueue
- [x] Engine production code: no direct `OrderHandler.HandleOrder` outside `StandAloneOrderHandler` + inbox
- [x] Standing pause unified: `FleetOrderCleanup.PauseStandingForPlayerIssue`
- [x] Goals block Standing; fleet UI shows goals via `GameProjector`
- [x] Tests use `IssueOrder` / `OrderEnqueue` (same path as live commands)
- [x] `533` tests passing (last full run)
- [x] Docs: `ORDER_ARCHITECTURE.md`, `DISCORD_BRANCH_INTRO.md` (optional share text)

## Still team / later (not blocking your private play)

- [ ] Network: inbox drained only on engine thread (async server)
- [ ] Dedupe ownership checks (translator meta vs order logic) — se5a / behindcurtain3
- [ ] Standing enqueue via inbox instead of direct `ActionList` (optional)
- [ ] Upstream PR slices off `origin/DevBranch` (visuals, tutorial, electricity, …)

## Quick verify

```powershell
cd Pulsar4X
dotnet test Pulsar4X.Tests/Pulsar4X.Tests.csproj
dotnet build Pulsar4X.sln   # close Client.Host first if DLL locked
```

## Branch tip

Current integration branch: **`dombreros_version_AI_vibecoded`**. Older work: `Electricity_system_1`, etc.
