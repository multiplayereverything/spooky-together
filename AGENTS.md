# Spooky Together development

Start with `../handoff.md` (current state, next steps) and `README.md` (behaviour, limitations). Keep both current along with `MODLOG.md`.

## Code graph
- **Game code:** the decompiled game is in `private/decompiled`, and its Graphify graph is in `private/decompiled/graphify-out`. For "where does the game do X", read `GRAPH_REPORT.md` and use `graphify query/explain` there before grepping.
- **Mod code:** the mod's own graph is `graphify-out/` in this folder. Run `graphify update .` after code changes.

## How the mod hooks the game (all in `src/Patches.cs`)
| Hook | Purpose |
| --- | --- |
| `HSMLoadRoomState.RunRoutine` (postfix wraps the iterator) | Host announces the room. Client waits for it and swaps `sceneToLoad` / `roomConfig` / `namedSpawnDoor` before the load starts. |
| `HSMRoomReadyState.EnterStateInternal` | Spawn door chosen from the shared room seed. |
| `HSMFloorState.TransferRequest(string,string,bool)` | Client doors become requests to the host. `TogetherRuntime.BypassTransfer` lets the mod's own calls through. |
| `HSMMenuState.StartMansion` | Host broadcasts the run. Clients are blocked unless `AllowStart` is set. |
| `HSMGameRootState.KillPlayer` | Downed and spectate instead of dying while a teammate stands. `AllowDeath` runs the native death. |
| `SpookyCharacterController.OnRoomReady` | Revive and spread players from the spawn. |
| `HSMBaseState.ExitState` | Detects the mansion ending. |
| `GameManager.OnApplicationFocus` | No focus-loss pause while connected. |
| `LockedDoorGroup`, `SelectiveExister`, `RoomController`, `RandomFlipZ`, `RandomYRot`, `LocalRoomControl.InitializeActive` | Per-room seeded `UnityEngine.Random`. |
| `SpecimenManager.InstantiateSpecimenFromPrefab` (postfix) | Host: net id plus spawn broadcast. Client: pair with the host's spawn, or mark it unpaired (removed after 6 s if never confirmed). |
| `SpecimenManager.StartSpecimenRechase` / `EndSpecimen` | Clients do not roll rechases and do not remove host-driven specimens themselves. |
| Transpiler on every specimen method calling `SpookyCharacterController.GetInstance().transform` (`Enemies.PatchSpecimenTargets`) | Host retargets to the nearest standing player through `TogetherRuntime.Target(this)`. |
| `SpookyCharacterController.Hurt` (both overloads), `UnknownSpecimen1Controller.DoHit` | Host sends hits aimed at remote players to them. Clients ignore hits from host-driven specimens. |
| `GameManager.ExitGame`, `ScareChairController.ChairKill` | Scares that quit the game or end everyone become a knock-down. |
| `PersistenceData.SetKey` / `ClearKey` / `AddToInventory` / `RemoveFromInventory*` | Shared run state, relayed through the host; 10 s checksum plus full snapshot repair. |
| `PickUpItem`, `PickupComponent`, `WeaponPickupComponent` `.OnInteract`; `DoorComponent` / `LocalDoorComponent` `.Unlock` | Object state by scene key; weapon pickups arm everyone. |
| `SendMessageOnHit.TakeHit`, `SpookyCharacterController.CurrentWeapon`, `SwingableWeaponConfig` / `M1WeaponConfig` `.DoHitTest` | Hit relay (specimens host-only), attacker's weapon during a replayed hit, swing display. |
| `HSMGameRootState.GotoKillscreen` | Endings: the host broadcasts; a client's call becomes a request to the host. |
| `EndlessSkipRoomUIControl.StartInteraction` | Host-only skip-room TV. |

## Repository
- **GitHub:** https://github.com/multiplayereverything/spooky-together (private). Commit and push after each version.
- **Identity:** commits use the repo's local identity (`multiplayereverything`, the GitHub no-reply address), never a personal email.
- **Before pushing:** run `python tests/rules-test.py`, and lint `git archive HEAD` with the Universal Modder publish check against `SpookyUnity_Data`. Keep personal paths out of scripts (use `$HOME`).
- **Ignored:** `private/`, `deps/`, `artifacts/`, `graphify-out/`, `*.ini`, `*.log`.
- **Patterns:** the `YokWare-multiplayer-game` skill holds the co-op design rules (host authority, sender rules, reset registration, offline tests).

## Rules
- **Statics:** a new static field is cleared in a reset method or marked `// process-scoped`, `// call-scoped` or `// lab-only` on its line; an empty `catch` says why on its line. `tests/rules-test.py` enforces both.
- **Build and test:** build with `scripts/build.ps1`. Test with `scripts/run-tour.ps1 -Tour <enemies|state|axe|ending|clientending|serverlist|soak>`.
  - Windows are hidden; up to 4 lab copies run: host (BepInEx), c1 (MelonLoader), c2 (BepInEx), c3 (MelonLoader).
  - Per-instance evidence is `private/lab-*/spooky-together-lab.log` (`ST-CHECK`, `ST-METRIC`, `ST-MODERROR`). Unity logs are in `private/lab-logs/`.
  - Never feed fake inventory ids: the game's HUD throws on unknown items inside a room-ready task and stalls its state machine.
- **Process safety:** stop only the PIDs in `private/lab-pids.txt`. Never kill a user-owned game process.
- **Lab copies:**
  - `private/lab-*` hardlink the exe and junction `SpookyUnity_Data`, so never edit game bytes through them.
  - `steam_appid.txt` exists only in the lab copies. The game's `SteamPlatformInstance` quits a direct launch without it.
- **Never distribute** game assemblies, `private/` (decompilation, labs), or `deps/`.
- **One loader per game copy.** The real install uses BepInEx (`BepInEx/plugins/SpookyTogether/`).
- **Protocol changes:** bump `Wire.Version` when the protocol changes.
