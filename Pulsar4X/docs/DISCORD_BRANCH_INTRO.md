# Introducing branch `dombreros_version_AI_vibecoded` (draft — optional Discord)

> **Private fork:** default is **local only**, no push. Use this text if/when you want dev feedback.

Hey — this is my long-running fork cleaned onto one branch for review. **Not meant to merge as one PR.** I’d like to split reviewable slices after we agree on scope.

## What’s in here (themes)

- **Client:** ship/body procedural visuals, jump visibility, tutorial mod (EN/DE)
- **Colony:** electricity / power UI
- **Fleet:** standing orders (priority + commitment + hysteresis), cross-system refuel, flagship/sync hardening
- **Engine/API:** Goals/Agent layer (OrdersAndAI-style), order-path cleanup + **engine command inbox**
- **Hygiene:** nullable cleanup, tests (goals, standing, API, visuals)

## Order stack (why it’s big)

Goals and Standing both drive the same `OrderableDB`. This branch keeps **Issue > Goal > Standing** and documents it in `docs/ORDER_ARCHITECTURE.md`. Player commands use `OrderEnqueue.Issued`; goals use `AssignGoal` for AI.

## Suggested review order

1. Tutorial mod / visuals (mostly client)
2. Electricity (colony + projector)
3. Standing + fleet sync (without goals)
4. Goals/Agent + inbox (needs the standing hooks)
5. Jump replication / misc API

## Honest notes

- AI-assisted; I’m still learning engine/API edges — feedback welcome
- Order path is centralized on **`EngineCommandInbox` + `OrderEnqueue`** (see `ORDER_ARCHITECTURE.md`)
- Some commits were mixed; this branch is the “integration” line; upstream should be file-scoped PRs off `DevBranch`

## Ask

Does this split match what you’d want to review? Anything that must land before goals (inbox, standing pause)?
