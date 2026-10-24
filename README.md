# Spooky Together

LAN/VPN co-op for **Spooky's Jump Scare Mansion: HD Renovation** (Steam build, Unity 2021.3 Mono), for 2–4 players. Separate builds exist for BepInEx 5 and MelonLoader 0.7; each copy of the game uses only one loader. Players on different loaders can play together.

## How to play
1. Install the mod (below) on every PC. Everyone needs the same game version and the same mod version.
2. Launch the game through Steam as usual.
3. Press **F5** to open the multiplayer panel, then set your name and colour.
4. One player presses **Host**. Port **27020** (TCP) must be reachable: forward it, or share a LAN or a VPN such as Tailscale or ZeroTier.
5. Everyone else either:
   - picks the host's game from the **server list** in the same panel (games on your network appear on their own), or
   - types the host's address and presses **Join**.
6. The host starts a run from the main menu (New Game or Continue). Every client is pulled into the same mansion automatically.

## Debug menu (F6)
Only the host can open it; on a client F6 just shows a message.

- **Rooms:**
  - Jump to any room by name (with a search box), go to the next room, reload the current room, or set the room counter.
  - The whole team moves with the host.
- **Specimens:**
  - See the live ones (health and position).
  - Spawn any type from your door, the way the game's scripted chasers arrive.
  - Clear them all.
- **Player:**
  - God mode, and refilling health and stamina.
  - Revive yourself if you're down.
  - Teleport to a teammate.
  - Give yourself any weapon, or any item (items join the team's shared inventory).
- **Info:**
  - Room, sequence and seed; room counter, floor and `violence`.
  - Shared-state checksum and room materials.
  - Frame time.
  - Each teammate's room, weapon and ping.
- **Errors:** recent game errors with the line they came from.
- **Save report:** writes all of the above, plus specimens, shared keys, the camera's screen effects and recent messages, to `UserData/SpookyTogether/report-<time>.txt`. Send that file with a bug report.

## What is shared
- **Levels:**
  - The host's game picks every room, and everyone loads the same room and spawn door.
  - Room randomness is seeded per room: locked doors, lights, flips, rotations, and which Dollhouse local-room specimens appear.
  - Opening an exit door moves the whole team on. Scripted moves (elevators, floor breaks, trams, Room 1000) follow the host.
- **Story state:**
  - The run's keys (puzzle flags, notes, `violence`, ending values) and the inventory are one shared copy: anyone's change reaches everyone.
  - Item pickups disappear for everyone, and unlocked doors unlock for everyone.
  - The host's specimen and jump-scare options apply to the run, because they change which ending branch a room takes.
  - Every 10 s the host checks the clients' copy and repairs any difference.
- **Enemies:** the host's game runs every specimen.
  - Each specimen chases the nearest standing player, and its hits land on the player it is chasing.
  - Clients see the same specimens in the same places, playing the same animation as the host's: spawned ones, ones placed in the room, Dollhouse local-room ones and endless-mode husk swarms.
  - Clients hear the same sounds as the host's monster (attacks, hits, growls). A client copy's own sound effects are muted.
- **The axe (and other weapons):**
  - A weapon picked up by one player is given to the whole team. Teammates carry and swing it visibly.
  - Every hit reaches every copy of what was hit: boarded doors, webs and walls break for everyone.
  - Specimens and the Taker boss take damage on the host only, so boss health and its good/bad ending are decided once.
  - Any player can reflect the Taker's fireball to open the boss up, and any player's hits count.
- **Endings:** any ending or credits sequence takes the whole team there together, whoever triggers it.
- **Teammates:** each appears as a coloured figure with a glow and a name tag, in the same room as you.
- **Getting caught:**
  - While a teammate is still standing, a lethal hit downs you instead of ending the run. You watch a teammate (click to switch) and revive at the next room.
  - If everyone goes down, the run ends with the normal game-over screen.
  - Two scares that would close your game are turned into normal knock-downs instead: Unknown Specimen 1's second pursuit and the scare chair.
- **Pausing:** losing window focus no longer pauses the game while connected.
- **Host-only actions:** starting a run and the endless-mode skip-room TV. A client who joins mid-run is pulled into the host's current room with the run's state.

## Known limitations
- **Taker boss attacks are per player.** Each player's copy of the boss throws its fireballs, hands, minions and pillars at that player, and they hurt that player. The host decides the boss's health, when it is open to hits, and the ending.
- **Per-player state:** health, stamina, the flashlight and notes on screen are per player.
- **Puzzle props:** a teammate's change shows at once on piano, furnace, barbed wire, doll pillars, hellroom candle icons, key-gated props and puzzle conditions. Other props update when you next enter the room.
- **The AFK Taker** (Specimen 9) appears only for the player who idles.
- **If the host disconnects,** clients keep playing alone; there is no host migration.
- **Starting the exe directly** without Steam makes the game quit and relaunch through Steam. Launch it through Steam.

## Install
- **BepInEx 5.4.23:**
  1. Extract BepInEx_win_x64 into the game folder.
  2. Copy `SpookyTogether.BepInEx.dll` to `BepInEx/plugins/SpookyTogether/`.
- **MelonLoader 0.7.x:**
  1. Install MelonLoader to `SpookyUnity.exe`.
  2. Copy `SpookyTogether.MelonLoader.dll` to `Mods/`.

Settings are saved in `UserData/SpookyTogether.ini`. The server list uses UDP port 27021 on your local network.

## Building and testing
- **Build:** `scripts/build.ps1` builds both loaders into `artifacts/`. It needs the .NET Framework `csc.exe` and the loader DLLs under `deps/`.
- **Test:** `scripts/run-tour.ps1 -Tour <name> [-Players 4]` starts up to four isolated lab copies, with windows hidden: a BepInEx host and MelonLoader/BepInEx clients. It runs one scripted tour, stops its own processes, and judges the run with `tests/verify-lab.py`. Results go to `artifacts/tests/<tour>-results.json`.

| Tour | What it checks |
| --- | --- |
| `enemies` | host spawn, client binding and tracking, retargeting, hit routing, host-only specimen hits, spawn/remove churn |
| `state` | keys and inventory from every player reach everyone; removals; `violence` |
| `axe` | weapon grant, hit relay to every copy, held weapon and swings seen by teammates |
| `ending` | host ending pulls everyone in |
| `takerbail` / `takerbad` / `takergood` | the real Taker room scripts: specimens-off credits; the boss fight with a client reflecting and chopping, where `violence` picks BadEnd or GoodEnd |
| `dollprops` | Dollhouse props (piano, furnace) refresh live on clients when the host changes their keys |
| `clientending` | a client's ending request is carried out by the host for everyone |
| `serverlist` | clients find the host by LAN broadcast and join |
| `reconnect` | two clients drop and rejoin three times mid-run; they resume without restarting and leave no avatars behind |
| `soak` | 10 minutes in the base mansion, 4 players, real doors, chasers queued the way the game queues them |
| `mansion -Mansion EM_Mansion / KH_Mansion / dollhouse` | the other campaigns through their real exits: the axe and a boarded door (Endless), the hospital hub, Dollhouse floors |

The lab players act like real players: they open real doors, use local doors, pick up the axe and chop boards.

Any tour accepts `-Lag <ms>`, which adds that one-way delay plus up to half again as jitter to every packet, to test slow links.

Every run also judges:
- error count;
- managed heap and object-count trends;
- entity desync;
- room sequence;
- the mod's own CPU time per frame (budget 2 ms).
