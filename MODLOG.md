# Spooky Together mod log

## 0.2.8 (2026-10-26): online through Steam, audit fixes, mutation checks, protocol 8
Requests: continue and improve (coop-research, coop-revive, coop-qa, coop-reverse-engineer, coop-sync-testing); then "this won't use the global relay server and work with steam api".

### Online play through Steam (replaces the global relay)
- **Removed:** the global relay mode, room codes and mods-info (`OnlineRelay.cs`, `RelayLink.cs`, `ModInfo.cs`, `-st-relay`, `-st-online`, `-st-modinfo`).
- **Steam lobby** (`SteamLobby.cs`): the host presses **Host on Steam**. Friends find the game in three ways:
  - the panel's **Steam games** list (public lobbies);
  - **Join Game** on the host's Steam profile;
  - an invite. An invite also launches the game (`+connect_lobby`).
- **Lobby data:** the host's name, mod version and protocol, player count and mansion. A version mismatch is refused before connecting.
- **Steam Networking Sockets P2P** (`SteamNet.cs`) carries the same packets as LAN, through Valve's relay, so nobody forwards a port. It runs on the main thread: messages are pumped in `Poll`, and connection changes arrive through the game's own `SteamAPI.RunCallbacks`.
- **A full game** refuses a fifth player with a reason. LAN / direct and the LAN server list are unchanged.
- **Lab:** `run-tour.ps1 -Steam` opens a real lobby tagged with the run. The copies share one Steam account, which can't connect to itself peer to peer, so they connect through the same Steam sockets over 127.0.0.1.

### Fixed (from a read-only audit per subsystem, each finding checked against the decompiled game)
- **Client floor drift.** A client's own floor state counts rooms too. It fires that floor's room events (`SetKey` at room N, relayed to everyone), and can end floors early. On the last floor that ended the client's run.
  - It drifted on Dollhouse "no increase" transfers, because clients always passed `false`.
  - It drifted when a client joined mid-run, because it started at floor 0, offset 0.
  - **Now:** the room packet carries the host's root-floor offset, and a client's root floor is moved to the host's floor and offset (`AlignFloor`). A forced follow keeps the host's room counter. A client's floor events stay local (`FloorConfigState.IncrementRoomOffset` prefix plus finalizer).
- **Rejoin after the host restarted:** the host restarted and began a new run 1, and a reconnecting client took it for its old run. The client then ignored rooms until its old sequence number. `StartRun` now carries a host session id.
- **Rejoin after playing alone:** a client that loaded rooms alone while disconnected now reloads into the host's room. Before, it stayed in its own room until the next door.
- **Specimens spawned while disconnected** are paired with the host's after a rejoin, or removed.
- **Endless husk hits** on a remote player landed on the host. The husk hurts with its swarm as the source; the hit now goes to the player that husk chases.
- **Client copies of specimens at run end** were only hidden by the game's `ClearSpecimens`. They stayed in `SpecimenManager.activeSpecimens` into the next run. They are now ended properly.
- **A teammate loading into the room** (one room behind) counts as standing, so the host's death in that moment no longer ends the run for everyone.
- **Room load:** a failure while announcing or following a room is logged and can't stall the game's room-load state machine.
- **Network layer:**
  - **Packet cap:** it is now per sender and about 30 s deep. One cap shared by all peers dropped whoever sent next after a ~8 s main-thread stall.
  - **Link drops:** every dropped link logs why (bad frame, send or receive failure, stall).
  - **Hosting:** a failed accept no longer ends hosting for everyone.
  - **Full game:** it refuses with "The game is full" instead of closing silently.
  - **Reused peer ids:** a packet read just before a link closed can't reach a new link that reuses its id.
  - **LAN list:** it is capped at 32 entries.
- **Build:** `build.ps1` finds the game through `-GameDir`, `$env:SPOOKY_GAME`, the parent folder or Steam's library folders. It broke when the source moved out of the game folder.
- **Lab:** a copy still exiting from the previous tour no longer fails the next tour's log cleanup.

### Added (testing)
- **Mutation check:** `scripts/mutants.ps1` turns one fix off at a time with `-st-mutate` (lab only) and expects its tour to fail. It covers retargeting, hurt routing, shared keys, weapon hits, elevator presses and stale locks. `rules-test.py` checks that every `Mutant("x")` switch has a tour.
- **`scripts/test-transport.ps1`** (was `test-relay.ps1`) checks the LAN path offline: joins, a full game's refusal, a bad frame's logged reason, and that hosting continues afterwards.
- **`docs/research.md`:** prior co-op work. Nothing exists for the HD Renovation; Project Recode plans couch co-op for the original game.
- **Lab robustness:**
  - The `state` tour waits until the whole team is in the room, and clients wait for the host's change rather than a fixed time.
  - `verify-lab.py` counts rooms a client followed into, not only the 10 s samples. A client that reached the room and its ending a second later was judged "fell behind".

### Evidence (4 players, windows hidden)
Another session's game lab ran at the same time, so CPU was at 76–100% throughout.

| Run | Result | Notes |
| --- | --- | --- |
| `tests/rules-test.py` | PASS | 6 mutation switches listed; versions 0.2.8 / protocol 8 |
| `scripts/test-transport.ps1` | 5/5 | LAN join, full-game refusal, hosting after leaves, bad frame's reason |
| `state -Steam` | PASS | real Steam lobby created and found in Steam's lobby list; Steam sockets; 0 errors |
| `enemies -Steam` | all checks passed | c3's heap trend was flagged during its late load (2.15 MB/10 s, short tour) |
| `state` (LAN) | PASS | 0 errors |
| quick suite | 9 of 11, then 11 of 11 | `clientending` and `takerbad` failed only on c3 "fell behind" (it reached the ending); both passed live after the verifier fix |
| `scripts/mutants.ps1` | 0 of 6 survived | every fix's tour fails with the fix off. `hurtrouting` was caught by `enemy.hit.host`, a check that also failed once without a mutant under load, so that catch is weak |
| `um publish check` (`git archive`) | PASS | no game files or personal paths |

Not tested: Steam P2P through Valve's relay between two Steam accounts (the lab has one account), the full suite (soak, lag, campaigns, reconnect), and the game launched by hand after installing.

Installed into the real game on 2026-10-26 (BepInEx, SHA256 274cf353…bc5f, the build the lab ran). BepInEx's loader files (`winhttp.dll`, `doorstop_config.ini`, `BepInEx/core`) were missing again and were restored from `deps/`; `BepInEx/config` was kept and backed up to `private/backups/install-2026-10-26`.

## 0.2.7 (2026-10-25): source rule test, cut-out sound fix
Request: "use it on spooky" (review the mod against the YokWare-multiplayer-game skill and apply what fits). Protocol unchanged (7).

### Fixed
- **Wrong cut-out sound on clients.** The host remembered the last sound any cut-out played and never cleared it, so a pop that played no sound sent the previous cut-out's sound (possibly from an earlier room or run). The host now uses a noted sound once (`Cutouts.cs`). `SpecimenOneController.Deploy` plays its sound synchronously, before the mod broadcasts the pop, so a pop with a sound still sends it.
- The host's picture and sound names a client receives for a cut-out are cleared once that pop is applied.
- **Overlapping messages:** a message that wrapped onto two lines in the top-right list was drawn under the next one (seen in a lab screenshot: "actively refused it" over "Connecting to..."). Each message now takes its real height (`DrawHud`).
- **Lab copies stuck in online mode:** a tour that stopped early left `Mode=online` in a lab copy's `SpookyTogether.ini`, so the next LAN tour's host tried an online room and every client was refused. `launch-lab.ps1` now resets each copy to LAN before launch; online tours still pass `-st-online` / `-st-relay`.

### Added
- **`tests/rules-test.py`:** source checks that need no game or build (seconds):
  - every `Op` has a handler, is built somewhere, and every op a client sends is accepted by `HostPacket`;
  - every mutable static is cleared by a reset method, set and cleared in a `try/finally`, or marked on its declaration (`// process-scoped`, `// call-scoped`, `// lab-only`);
  - every empty `catch` says why on its line;
  - `ModVersion` matches MODLOG's top entry and `Wire.Version` its newest protocol.
- The 45 static fields and 20 empty catches now say what they are and why (comments only).
- **Git:** the source is in https://github.com/multiplayereverything/spooky-together (private).

- **Early stop judged a loading client as behind:** tours stop once every expected check passed, and a client still loading into the host's room was then reported as "fell behind the host" (`elevator`). The early stop now also waits while a client is behind (`run-tour.ps1`); a client that never catches up still fails at the deadline.

### Evidence (4 players)
| Run | Result | Notes |
| --- | --- | --- |
| `tests/rules-test.py` | PASS | 35 ops handled and built, 7 client ops accepted by the host, 45 statics covered, 20 empty catches explained, versions agree |
| quick suite | 9 of 11 | enemies, state, axe, clientending, takerbad, cutouts, stuck, look, serverlist passed; `elevator` and `debug` failed (below) |
| `debug` rerun | PASS | the first run's "C3 entity desync worsening (0.54 m per 10 s)" did not recur: flaky |
| `elevator` reruns after the early-stop fix | PASS, PASS | the failures were the early stop racing a loading client |
| HUD screenshot | checked | a lab client with no host lists wrapped "actively refused" errors without overlap |

Not run on 0.2.7: the full suite (soak, lag, campaigns, endings, reconnect).

Installed into the real game on 2026-10-25 (SHA256 34982f3d…8830, the build the lab ran). BepInEx 5.4.23.5 was missing from the game folder and was restored from `deps/`; the game was not launched afterwards.

### Audit against the skill (not changed)
- Already in place: guard flags cleared in `finally`, run-state checksum with full repair, version and game-hash check at hello, nearest-player retarget transpiler, host-decided cut-outs and seeded room randomness, a `reconnect` tour.
- Not added (the handoff limits this mod to what multiplayer compatibility needs): voice chat, host migration, a sectioned desync checker for specimens.
- `Transport` drops a link on a bad frame without logging why (the catch ends the read loop); noted for a later change.

## 0.2.6 (2026-10-22): F6 debug menu is host-only
- Only the host can open the F6 debug menu; on a connected client F6 shows a message instead.
- The `debug` tour now has the host use the menu (travel, spawn, report) and clients check they follow.

## 0.2.4 (2026-10-22): fixes from the first real sessions, protocol 7

### Reported in play, then fixed
- **Specimen 1 cut-outs:** they faced different sides, popped for one player only, and showed different pictures.
  - Their side (`SpecimenOneController.Start`, also `UnknownSpec5Spec1Controller.Start`) is seeded per room.
  - Whether one pops, and its picture and sound, are decided by the host. The first player to trigger it makes it pop for everyone (`Cutouts.cs`).
- **Elevators:** only the presser's elevator rode. `ElevatorButtonControl` / `KHElevatorButton` presses are relayed, so the ride starts for everyone.
- **Stuck after an arcade game:** a teammate's door unloaded the room while a minigame held a movement lock, and the player stayed frozen.
  - On a room change forced by a teammate, a running minigame is ended properly, and locks left by the old room are released when the next room starts loading.
  - Lock counters are never left negative (`StaleLocks.cs`). This also covers terminals, puzzles, savepoints and modals.
- **Rooms looked different** (walls and floor on another floor's materials). The host sends the wall, floor and ceiling materials it uses for every room, and again when a specimen re-skins the floor. Client floor getters use them (`RoomLook.cs`).
- **Fast monsters trailed behind on clients.** Puppets lead the last snapshot by the specimen's velocity (at most 150 ms).

### Testing changes
- **Early exit:** tours stop as soon as every expected check has passed (the deadline remains). Endurance runs keep their full length.
- **Quick suite:** `lab-suite.ps1 -Quick` runs the essential checks in about 15–20 minutes.
- **Lab port 27120,** so tests run beside a real game hosting on 27020.
- **Error text in lab logs:** `ST-ERROR` lines record the message and first stack line of each game error.
- **Known game errors** a host-only control run also produces are listed and do not fail a sweep: two Endless monsters spawned outside their home rooms, and hidden windows refusing a cursor lock.
- **New tours:**
  - `cutouts` (side and picture match the host's);
  - `elevator` (base `Elevator_01` and the hospital lift);
  - `stuck` (a busy player can move after a teammate's door);
  - `look` (42 room checks with a deliberately drifted client, 0 mismatches);
  - `spec6` (screen brightness after Specimen 6 ends; no black screen reproduced);
  - `allspecs` (every chaser config spawned one at a time in a fixed room; all 26 types — Specimens 2–8 and 10–13 plus 15 Endless monsters — pass on the host and all 3 clients).

### Open
- **A black screen after Specimen 6, reported in play:** not reproduced. The friend's `Player.log` from right after it happens is needed.
- **Arcade screens:** they are per player, so a teammate sees their own idle cabinet.

## 0.2.2 (2026-10-22): per-player Taker attacks, monster sound sync, network-path test, protocol 5

### Changes
- **Taker boss:**
  - Its attacks (fireballs, hands, minions, pillars) are spawned by each player's own copy and aimed at that player, so they hurt locally on every machine. Previously only the host's attacks did damage.
  - Its health, the windows when it can be hit, and the ending stay host-authoritative: hits on the host's boss, relayed reflects, and hp mirrored in snapshots.
  - It is excluded from the retarget transpiler but stays host-bound. One intermediate build dropped it from binding; the takerbad tour caught that, and it is fixed.
- **Hp in snapshots:** every specimen's health follows the host in snapshots, for example the Taker's phase change at 100 hp.
- **Monster sounds:** `AudioSource.PlayOneShot` on host specimens is sent as a cue (net id, AudioSource path, clip name, volume) and played on the client copy. The client copy's own one-shots are muted. In the enemies tour each client played 32 host cues and muted 13–18 of its own.
- **Network path:** `run-tour.ps1 -Address` makes clients join through a non-loopback address. The suite runs a soak through this PC's LAN/VPN address (Radmin VPN 26.92.229.172) with 120 ms lag plus jitter.

### Evidence: final suite on the installed build
- **Build:** BepInEx SHA256 `b7c968920901f2f3…`, the same file installed. MelonLoader `ad59f6fbd094f5f5…`.
- **Result:** 0 of 17 failed. Every run had 4 players and hidden windows, with 0 game errors on every instance.

| Tour | Host room seq | Mod ms/frame (worst median) | Mod objects (max) |
| --- | --- | --- | --- |
| axe | 4 | 0.13 | 4 |
| clientending | 2 | 0.06 | 4 |
| dollprops | 18 | 0.22 | 4 |
| ending | 2 | 0.06 | 4 |
| enemies | 5 | 0.16 | 6 |
| enemies, 150 ms lag | 5 | 0.15 | 6 |
| mansion EM_Mansion | 16 | 0.17 | 6 |
| mansion KH_Mansion | 17 | 0.16 | 4 |
| mansion dollhouse | 17 | 0.21 | 4 |
| reconnect | 2 | 0.17 | 4 |
| serverlist | 4 | 0.12 | 4 |
| soak | 48 | 0.17 | 4 |
| soak via 26.92.229.172, 120 ms lag | 48 | 0.16 | 4 |
| state | 5 | 0.17 | 4 |
| takerbad | 3 | 0.06 | 4 |
| takerbail | 2 | 0.06 | 4 |
| takergood | 3 | 0.06 | 4 |

### Still not verified
- Physically separate PCs played by people. The lab uses one PC, with real LAN/VPN interfaces and simulated lag.

## 0.2.1 (2026-10-22): real ending paths, animation sync, live props, slow-link tests, protocol 4

### Gaps closed (from the goal review)
- **Taker boss:**
  - **Bug:** a client's fireball reflect only opened its own copy of the boss, while damage counts on the host. So clients could never damage the Taker.
  - **Fix:** `TakerBossControl.FireballReflect` is now relayed: a client's reflect opens the host's boss, and the host's reflect is mirrored to everyone.
- **Animations:** host snapshots carry each specimen's animation state, and client puppets play it.
  - Animator: the state hash.
  - Legacy `Animation`: the clip index, used by EMKHMonster5, EMSpec4, KHMonster5, Spec5, Spec10, Spec11 and UnknownSpecimen2.
- **Live props (`PropRefresh.cs`):** after a teammate's state change, the props in your room refresh at once.
  - Piano, furnace, barbed wire: their room-ready methods, which register once in Awake and only set visibility.
  - Doll pillars: the old doll is removed first.
  - Hellroom: candle icons only.
  - Key-gated props: `PersistentValueExister`.
  - Puzzle conditions: newly met ones only.
- **Transport:** the per-link send queue limit went from 128 to 512 packets, so a link stalling for a couple of seconds no longer drops the player.
- **Lab:** `-st-lag <ms>` adds one-way delay plus up to half again as jitter, in order.

### New tours
- **Taker:**
  - `takerbail`: End04's transfer to End_05, specimens off, then the room's own `BailToCredits` sends everyone to Credits.
  - `takerbad` / `takergood`: the boss fight. C1 reflects and chops the boss with relayed hits (25 × 8 = 200 hp on the host's boss), and the shared `violence` picks BadEnd or GoodEnd for all 4.
- **`dollprops`:** the host sets `DOLL_PIANO` and toggles `DOLL_FURNACE_DOOR`, and clients refresh those props with no failures.
- **Lag runs:** `enemies -Lag 150` and `soak -Lag 150`; both pass.
- **Enemies tour additions:** animation checks; a legacy-animated specimen spawned like Crowd Control's chaser (`rechase` plus `StartRoom(playerSpawn)`).

### Evidence: final suite on the installed build
- **Build:** BepInEx SHA256 `4e8e7cf015af0a70…`, the same file installed in `BepInEx/plugins/SpookyTogether/`. MelonLoader `2c1341eaae8d6a7a…`.
- **Players:** 4, with windows hidden: host BepInEx, c1 MelonLoader, c2 BepInEx, c3 MelonLoader.
- **Result:** `scripts/lab-suite.ps1`, 0 of 16 failed. Per-instance logs are in `artifacts/tests/logs/<tour>/`.

| Tour | Result | Host room seq | Game errors (all 4) | Mod ms/frame (worst median) | Mod objects (max) |
| --- | --- | --- | --- | --- | --- |
| axe | PASS | 4 | 0 | 0.12 | 4 |
| clientending | PASS | 2 | 0 | 0.07 | 4 |
| dollprops | PASS | 18 | 0 | 0.21 | 4 |
| ending | PASS | 2 | 0 | 0.07 | 4 |
| enemies | PASS | 5 | 0 | 0.16 | 6 |
| enemies, 150 ms lag + jitter | PASS | 5 | 0 | 0.16 | 6 |
| mansion EM_Mansion | PASS | 16 | 0 | 0.18 | 5 |
| mansion KH_Mansion | PASS | 17 | 0 | 0.16 | 4 |
| mansion dollhouse | PASS | 17 | 0 | 0.20 | 4 |
| reconnect | PASS | 2 | 0 | 0.17 | 4 |
| serverlist | PASS | 4 | 0 | 0.11 | 4 |
| soak (10 min) | PASS | 48 | 0 | 0.17 | 4 |
| state | PASS | 5 | 0 | 0.15 | 4 |
| takerbad | PASS | 3 | 0 | 0.07 | 4 |
| takerbail | PASS | 2 | 0 | 0.07 | 4 |
| takergood | PASS | 3 | 0 | 0.07 | 4 |

A separate 10-minute soak under 150 ms one-way lag with jitter also passed: 48 rooms, 0 errors, flat heap, 55/55 checksums.

### Still not verified
- Physically separate PCs. Lag is simulated in the lab.
- Human play-throughs.
- Specimen sounds stay per copy.
- Taker projectiles aim at the host's player.

## 0.2.0 (2026-10-22): enemies, story state, axe, endings, server list, 4 players, protocol 3

### Requirements (user, 2026-10-22)
- Sync all levels, all enemies, the ways to reach the endings, and the axe.
- A server list.
- Up to 4 players.
- No accumulating errors, leaks or worsening desync.
- Baldi-style tests with hidden windows.
- Only what multiplayer compatibility needs.

### Recon (two mapping agents over `private/decompiled`)
- **Spawns:** all SpecimenManager spawns end in `InstantiateSpecimenFromPrefab`. Spawns outside it are scene-placed KH/EM monsters, the Taker boss, Dollhouse `LocalRoomSpecimen` and husk swarms.
- **Targeting:** specimens read `SpookyCharacterController.GetInstance().transform` inline in about 200 places.
- **Weapon hits:** all of them end in `SendMessageOnHit.TakeHit` (scene UnityEvents).
- **Endings** use `GotoKillscreen`. Their state lives in `PersistenceData` keys and inventory, for example `violence > 20`, which decides the Taker bad end.
- **Game-quitting paths:** `UnknownSpecimen1Controller.DoHit` (fake death via a room move, then `GameManager.ExitGame`) and the scare chair's `GotoKillscreen`.

### Built
- **Enemies (`Enemies.cs`):**
  - net ids for SpecimenManager spawns, plus scene-key binding (hierarchy path; husks by swarm index);
  - a host transpiler that retargets every specimen method to `Target(this)`, the nearest standing player, with hysteresis;
  - hit routing to remote players;
  - client puppets driven by 15 Hz snapshots, never removed locally, unconfirmed local spawns removed after 6 s, hp copied from the host.
- **Shared state (`SharedState.cs`):**
  - keys and inventory relayed through the host;
  - a 10 s checksum with full-snapshot repair;
  - pickups, door unlocks and weapon pickups by scene key;
  - the host's specimen and jump-scare options.
- **Combat (`Combat.cs`):**
  - hit relay;
  - specimen hits applied on the host only, so Taker boss hp and the ending are decided once;
  - the attacker's weapon used during a replayed hit;
  - held axe and swings on avatars.
- **Endings (`Endings.cs`):**
  - the host broadcasts `GotoKillscreen`, and a client's call becomes a request to the host;
  - the scare chair and the ExitGame scare become knock-downs;
  - the endless-mode skip TV is host-only.
- **Server list (`LanDiscovery.cs`, ported from Baldi Together):** UDP 27021 beacon, list in the F5 panel.
- **Lab (`Lab.cs`, `Tours.cs`, `scripts/run-tour.ps1`, `scripts/lab-suite.ps1`, `tests/verify-lab.py`):**
  - four isolated copies with hidden windows: host (BepInEx), c1 (MelonLoader), c2 (BepInEx), c3 (MelonLoader);
  - per-instance `ST-CHECK` and `ST-METRIC` logs;
  - verdicts on checks, error growth, heap and object trends, desync, room sequence, and mod CPU time (2 ms budget).

### Bugs found by the tests and fixed
- A specimen's first target choice was blocked by hysteresis, so players standing together were never targeted.
- Any exception while applying a packet dropped that client; the game's HUD threw on an inventory item and all three clients were kicked. Now only malformed packets drop a peer.
- A reconnecting client restarted the whole run. It now resumes, and its specimens re-pair.
- Unknown inventory ids stalled the game's state machine through `InGameUIComponent.RefreshInventory`. Only ids in `allInventoryItems` are mirrored.
- Two door requests at once could skip a room. Requests now carry their room sequence.
- Client copies of specimens took axe damage locally, which could end the Taker fight early on one machine. Specimen hits are now host-only.
- Avatar materials were never freed (a small leak per join and leave).

- Lab bots that forced raw transfers loaded rooms a mode never reaches, stalling the game. Bots now use real doors, local doors, walk-in exits, the axe on boarded doors, and the skip-TV exit.
- Chasers that reached a client after its room was ready had no spawn door (NRE every frame). Late puppets now get the room's door. `EmergenceUtil.TweenToPlayerSpawn` / `FinishSpawn` skip while there is no door; the game's own skip-TV queue hits the same case.

### Evidence (final build, 4 players: host BepInEx, c1 MelonLoader, c2 BepInEx, c3 MelonLoader, all windows hidden)
Results are in `artifacts/tests/*-results.json`, with per-instance logs in `artifacts/tests/logs/<tour>/`.

| Tour | Result | Host room seq | Game errors (all 4) | Mod ms/frame (worst median) |
| --- | --- | --- | --- | --- |
| axe | PASS | 4 | 0 | 0.13 |
| clientending | PASS | 2 | 0 | 0.06 |
| ending | PASS | 2 | 0 | 0.06 |
| enemies | PASS | 4 | 0 | 0.14 |
| mansion EM_Mansion (Endless) | PASS | 16 | 0 | 0.17 |
| mansion KH_Mansion | PASS | 17 | 0 | 0.16 |
| mansion dollhouse | PASS | 16 | 0 | 0.22 |
| reconnect | PASS | 2 | 0 | 0.16 |
| serverlist | PASS | 4 | 0 | 0.12 |
| soak (10 min, base mansion) | PASS | 48 | 0 | 0.17 |
| state | PASS | 3 | 0 | 0.11 |

Across every run, on every instance:
- the managed heap stayed flat while playing rooms;
- the mod's own GameObjects stayed at 4–7, within the 4-player bound of 11;
- state checksums always matched;
- specimen desync stayed under 2.5 m of interpolation lag, with no growth.

The mod's own CPU cost is about 0.03–0.22 ms per frame.

### Not verified
- A real multi-PC session (latency, VPN).
- A full playthrough to each ending with people. The lab drives endings through `GotoKillscreen("Credits")`, not by fighting the Taker boss.
- Remote players' specimen animations are each copy's own.

## 0.1.0 (2026-10-22): first playable co-op, protocol 1

### Recon
- **Game:** Unity 2021.3.21 Mono, Steam app 577690.
- **Decompilation:** `Assembly-CSharp.dll` decompiled with ilspycmd into `private/decompiled` (929 files). Graphify graph: 6763 nodes, 272 communities. Game SHA256 `f657ebc2…ef7`.
- **Rooms:**
  - Rooms are pre-built additive scenes. `TiledRoom` geometry is baked, not generated at runtime.
  - `HSMFloorState.RunRoutine` picks room ids through `GameManager.NextInt()` (the global `UnityEngine.Random`). Every load passes through `HSMLoadRoomState`.
  - All doors call `HSMFloorState.TransferRequest`.
- **Player and specimens:**
  - The player is the singleton `SpookyCharacterController`; 228 files call `GetInstance()`.
  - Specimens re-read that singleton every frame, and there is no common target field. This is why specimens are per player in 0.1.0.
- **Death and pause:**
  - Every death goes through `HSMGameRootState.KillPlayer`.
  - Pausing sets `MansionManager.isPaused`; `Time.timeScale` is never used.
  - Focus loss auto-pauses through `GameManager.OnApplicationFocus`.

### Built
- **Source files:** `Transport.cs` (TCP framing, ported from Baldi Together), `Runtime.cs` (lifecycle, F5 panel, HUD), `Net.cs` (packets, star relay), `Rooms.cs` (host-led rooms, seeded furniture, door requests), `Avatars.cs` (teammate figures, name tags), `Downed.cs` (down, spectate, revive, team game over), `Patches.cs` (13 Harmony patches).
- **Loaders:** BepInEx and MelonLoader builds are produced from the same source.

### Evidence
Lab: BepInEx host plus MelonLoader client, on one PC, from isolated copies. Logs are in `private/lab-logs`; screenshots are `private/lab-*/shot-*.png`.

| Check | Result |
| --- | --- |
| Patches | 13/13 applied on both loaders |
| Run start | Host auto-start pulled the client into BASEMANSION |
| Room sync | Client loaded the host's room on every transition (client's own picks such as `SM_01` and `Hall_20` were replaced). The spawn door matched, including 1/2 on `Hall_10`. 4+ rooms per run. |
| Client doors | A client door request moved both players |
| Avatars | Visible in both directions; players spawn 1.1 m apart after the spread fix |
| Downed | Client downed in room 2 and spectated the host, then revived with full health in room 3 |
| Team game over | Host down → client last-standing death → host broadcast game over → native death on both → next run started cleanly |
| Exceptions | None in either log |

### Fixed during testing
- A host losing focus paused its whole game. Now patched out while connected.
- Players spawned inside each other. They are now spread by slot through the CharacterController.
- A game over received before a client entered the run left a pending start. It is now cleared.

### Not yet verified
- A real session across two PCs (latency, NAT or VPN).
- More than two players.
- Long runs through floor transitions (elevator room) and endless mode.
- Rooms with scripted `TransferRequest` calls, such as Unknown Specimen 1's fake death or Dollhouse events. These are forwarded to the host and move everyone.
