# Private fork status (`dombreros_version_AI_vibecoded`)

**Local only — do not push** unless you explicitly choose to (e.g. `mine` remote for backup).

## Done (order / command stack)

- [x] `EngineCommandInbox` — queue + drain (submit, sim sub-pulse, UI pump while paused)
- [x] Continuous engine command pump (~50ms) in `EngineGameServer` — drains while paused
- [x] `OrderEnqueue` — single entry for Issued / FromGoal / Standing / generic Enqueue
- [x] Standing enqueue via `OrderEnqueue.Standing` (not direct `ActionList.Add`)
- [x] Ownership checks stripped from orders — `IsCommandValid` is entity-alive + resolve refs; auth is translator / server
- [x] Engine production code: no direct `OrderHandler.HandleOrder` outside `StandAloneOrderHandler` + inbox
- [x] Standing pause unified: `FleetOrderCleanup.PauseStandingForPlayerIssue`
- [x] Goals block Standing when active; fleet UI shows goals via `GameProjector`
- [x] Design: Goals = AI / auto-freewill only; Standing stays for player conditions (Goals do not replace Standing)
- [x] Tests use `QueueOrder` / `OrderEnqueue` (same path as live commands)
- [x] Docs: `ORDER_ARCHITECTURE.md`, `DISCORD_BRANCH_INTRO.md` (optional share text)

## Still team / later (not blocking your private play)

- [ ] Network: optional async server thread (drain can stay sync inside the continuous pump)
- [ ] Optional UI for freewill/auto-mode that assigns Goals (Standing UI stays as-is)

## Quick verify

```powershell
cd Pulsar4X
dotnet test Pulsar4X.Tests/Pulsar4X.Tests.csproj
dotnet build Pulsar4X.sln   # close Client.Host first if DLL locked
```

## Branch tip

Current integration branch: **`dombreros_version_AI_vibecoded`**. Older work: `Electricity_system_1`, etc.
