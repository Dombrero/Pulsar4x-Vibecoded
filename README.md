# Pulsar4x
[![Build status](https://ci.appveyor.com/api/projects/status/owpp4y7ruyn0skm1/branch/Master?svg=true)](https://ci.appveyor.com/project/intercross21/pulsar4x/branch/Master)
[![Join the chat at https://gitter.im/Pulsar4x/Lobby](https://badges.gitter.im/Pulsar4x/Lobby.svg)](https://gitter.im/Pulsar4x/Lobby?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

# Welcome to Pulsar4x.

A fan-made project to recreate a more user-friendly and better optimized version of Aurora, a 4x space sim created by Steve Walmsley. Pulsar4x is developed in C#. The long term goal of the project was originally to reproduce a feature-complete clone of Aurora. However, due to Steve Walmsley picking up development on his own C# version of Aurora, this has started to veer off on its own path, and has become a flexible, moddable aurora-like engine.


#### How to [Compile](https://github.com/Pulsar4xDevs/Pulsar4x/wiki/Compilation).
#### Here are some [FAQs](https://github.com/Pulsar4xDevs/Pulsar4x/wiki/FAQ).
#### Here are our current [Tasks](https://github.com/Pulsar4xDevs/Pulsar4x/wiki/Issues-&-Projects).
#### Our current [Projects](https://github.com/Pulsar4xDevs/Pulsar4x/projects).
#### Documentation is available on the [Wiki](https://github.com/Pulsar4xDevs/Pulsar4x/wiki).
#### We track bugs in [Issues](https://github.com/Pulsar4xDevs/Pulsar4x/issues).

### Contributing:
* Create a fork of the project.
* Ensure you're on the DevBranch.
* Find an issue you'd like to look at, either by looking through the issues on github or something that looks like it needs doing just from compiling and running the project (some low hanging fruit ie ui improvements, we've not created issues for).
* Ask questions, either in Discord, creating a github issue, or gitter.
* Fix issue.
* Create a pull request.
* Wait for it to get looked at, fix any minor pointers brought up in the pull request if any.
* Celebrate your first successfull PR to the project.
Once you've got a couple of PRs under your belt we'll consider adding you to the group which will give you permisisons to push directly.
***

### Community

* Discord [Server](https://discord.gg/3uwCQSn) (primary)
* Aurora [Subforum](http://aurora2.pentarch.org/index.php?board=169.0)
* Gitter [Community](https://gitter.im/Pulsar4x/Lobby)
* IRC [Channel](http://webchat.freenode.net/) (inactive)
  * Enter #Pulsar4x as your Channel.

***

### What’s different in this fork (Dombrero / `dombreros_version_AI_vibecoded`)

This is a **personal long-lived fork** of [Pulsar4xDevs/Pulsar4x](https://github.com/Pulsar4xDevs/Pulsar4x) — not a clean drop-in for `DevBranch`. Feel free to browse and cherry-pick; expect ongoing fixes.

**Compared to upstream, this branch mainly adds / changes:**

* **Ship visuals** — procedural ship sprites on the system map and in the designer  
  (`Pulsar4X.Client/ShipVisuals/`, part PNGs under `Resources/ship-parts/`, `ShipIcon` / `ShipMapTextureCache`, designer helpers `ShipVisualRawBmp` / `ShipVisualStateMapper`)
* **Body / planet visuals** — procedural body textures from survey stats (temp, atmosphere, minerals, type)  
  (`Pulsar4X.Client/BodyVisuals/`: `BodyVisualStateFactory`, `BodyVisualComposer`, `SolBodyPresets`, extreme-heat ring); unsurveyed bodies stay a featureless grey fog-of-war disk until geo-survey completes; wired through `SysBodyIcon` / `StarIcon` / `SystemMapRendering`
* **Order / command path** — engine command inbox + continuous ~50ms pump (also while paused), `OrderEnqueue` as the single door into `HandleOrder`, ownership/auth on translator/server only; see `Pulsar4X/docs/ORDER_ARCHITECTURE.md`
* **Standing orders** — list priority + commitment + fuel/energy hysteresis; cross-system refuel; flagship / standing sync hardening; standing pause lined up with Issue / top-level goals
* **Goals / Agent layer** — OrdersAndAI-style planners for **AI / optional auto-freewill only** (does **not** replace player Standing)
* **Colony electricity / power** — generation, storage, colony power UI / projector bits
* **Tutorial mod** — English + German guide / start helpers (`GameData/tutorial-mod/`)
* **Jump visibility + client replication** — jump-point / map visibility and related client sync after transit
* **Nullable / NRT cleanup** across engine, client, and tests

Upstream Pulsar4X recruiting / contribution flow still lives on the main project Discord and [Pulsar4xDevs/Pulsar4x](https://github.com/Pulsar4xDevs/Pulsar4x).


***
