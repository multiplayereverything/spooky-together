# Spooky Together handoff

Updated 2026-10-27. Paths are relative to this folder (the mod's git repo). The game is in Steam's `steamapps/common/Spooky's Jump Scare Mansion HD Renovation` (`<game>` below).

## Goal
A co-op multiplayer mod for Spooky's Jump Scare Mansion HD Renovation, for up to 4 players, with BepInEx and MelonLoader builds. It is modelled on Baldi Together.

The user's standing requirements:
- all levels, enemies, ways to reach the endings, and the axe synced;
- a server list;
- 4 players;
- no accumulating errors, leaks or worsening desync;
- Baldi-style lab tests with game windows hidden;
- only what multiplayer compatibility needs;
- online play through the Steam API, not the global relay server (2026-10-26).

## Current version: 0.2.11 (pre-release), protocol 9
- **Release:** v0.2.11 fixes 0.2.10's known issue (an ending held for a room load that never came). See `MODLOG.md`.
- **Real play:** the owner played with three or more friends on separate PCs and Steam accounts, four players together, with no problems (2026-10-27).
- **Website:** graded A, "available". The Website session (local_211a3291-3165-4fdf-8d32-3da6780cf595) keeps it in sync; Co-op Anything write-up: PR cranebeaker/coop-anything#221 (branch `game/spooky-jump-scare-mansion` on multiplayereverything/coop-anything).
- **Working rules (user, 2026-10-27):** don't stop to ask; make the call and note it here and in MODLOG. Lab copies start minimized and small (see AGENTS.md), one tour at a time. Use the Co-op Anything MCP and coop-* skills and say which page was used. End each batch with a results table.

## Setting up on a new PC
Everything below except the GitHub repo lives outside git; rebuild it as follows.
1. **Clone:** `git clone https://github.com/multiplayereverything/spooky-together "Spooky Mulitplayer Mod"`. Set the repo-local identity:
   - `git config user.name multiplayereverything`
   - `git config user.email 347356261+multiplayereverything@users.noreply.github.com`
2. **Game:** install Spooky's Jump Scare Mansion: HD Renovation through Steam (Unity 2021.3 Mono). `build.ps1` finds it through Steam's library folders; otherwise pass `-GameDir` or set `$env:SPOOKY_GAME`.
3. **Loaders (`deps/`, not in git):**
   - `deps/BepInEx-5.4.23.5/`: the BepInEx 5.4.23.5 x64 release, extracted. The build references `BepInEx/core`; `package.ps1` ships it inside the BepInEx ZIP.
   - `deps/MelonLoader-0.7.3/`: the MelonLoader 0.7.3 x64 release, extracted. The build references `MelonLoader/net472`.
   - `deps/ilspycmd/` (optional) decompiles the game.
4. **Tools:**
   - Windows .NET Framework 4.x `csc.exe`, which `build.ps1` uses (C# 5);
   - Python 3 for `tests/rules-test.py` and `tests/verify-lab.py`;
   - `gh`, signed in as `multiplayereverything`, for releases;
   - `um`, the Universal Modder CLI, for `um publish check`;
   - `graphify` (optional), for the code graphs.
5. **Build:** `scripts/build.ps1`. Output goes to `artifacts/BepInEx` and `artifacts/MelonLoader`. `scripts/package.ps1` makes the release ZIPs in `artifacts/release`.
6. **Install for play:** extract `BepInEx-5.4.23.5` into `<game>`, then copy `artifacts/BepInEx/SpookyTogether.BepInEx.dll` to `<game>/BepInEx/plugins/SpookyTogether/`. Use one loader per game copy.
7. **Lab copies (`private/lab-host`, `lab-client`, `lab-c2`, `lab-c3`):** made by hand, not by a script. Each one has:
   - **links into `<game>`:** hardlinks of `SpookyUnity.exe`, `UnityPlayer.dll` and `UnityCrashHandler64.exe`; junctions for `SpookyUnity_Data` and `MonoBleedingEdge`;
   - **a loader:** host and c2 get BepInEx (`winhttp.dll`, `doorstop_config.ini`, `BepInEx/`); client (c1) and c3 get MelonLoader (`version.dll`, `MelonLoader/`, `Mods/`, `Plugins/`, `UserLibs/`);
   - **`steam_appid.txt`** containing `577690`, the game's Steam app id. Without it the game relaunches through Steam and quits.

   Never edit game bytes through these links. `scripts/launch-lab.ps1` copies the fresh build into each copy and starts them hidden; `run-tour.ps1`, `lab-suite.ps1` and `mutants.ps1` drive them. Hardlinks must be on the same drive as `<game>`, so they may need a re-link if the drive differs.
8. **Decompiled game (optional, for research):** `private/decompiled/` (about 23 MB) and its graph. Regenerate it with `ilspycmd` from `<game>/SpookyUnity_Data/Managed/Assembly-CSharp.dll`, or copy it over by hand.

## Moved to a new PC (2026-10-27)
- The repo, `deps/` and `private/` (decompilation, lab copies) were carried over. The lab copies had arrived as full copies of the game; they were checked against the install (189 files, same Assembly-CSharp) and re-linked (junctions and hardlinks) to `C:/Program Files (x86)/Steam/steamapps/common/Spooky's Jump Scare Mansion HD Renovation`.
- BepInEx and the mod are not installed in that game folder on this PC (the lab copies don't need it). Install them as in step 6 above before playing here.

## State details: 0.2.11, protocol 9
- **Source:** https://github.com/multiplayereverything/spooky-together (private, `main`). Commit as `multiplayereverything` with the GitHub no-reply address, and push after each version.
- **Moved 2026-10-25:** the source moved from `<game>/MultiplayerMod` to `ai-work-pc-games/Spooky Mulitplayer Mod`. `build.ps1` finds the game itself; the lab copies (`private/lab-*`) still hardlink and junction into `<game>`.
- **Online:** Steam lobbies plus Steam Networking Sockets P2P (`SteamLobby.cs`, `SteamNet.cs`). The global relay, room codes and mods-info are gone. LAN / direct (TCP 27020) and the LAN list (UDP 27021) remain.
- **Tests (0.2.11):** the full suite passes 29/29 on the new PC with the lab windows minimized (`pairing` needed the 5-sample heap rule, see `MODLOG.md`). 16 mutation switches; the seven touched in 0.2.10-0.2.11 were rerun and all caught. `state` and `enemies` over Steam ran on 0.2.8.
- **Releases:** every version is a tagged GitHub release, pre-release while untested (the Discord bot announces them). v0.2.8, v0.2.9 (2026-10-26) and v0.2.10 (2026-10-27) are published as pre-releases with their ZIPs. `scripts/package.ps1` builds the ZIPs; lint them with `um publish check` before uploading.
- **Installed on the old PC:** the 0.2.10 BepInEx build in `<game>/BepInEx/plugins/SpookyTogether/`, with BepInEx 5.4.23.5.

## Next steps
1. **Real play (done, reported 2026-10-27):** the owner played with three or more friends on separate PCs and separate Steam accounts, four players together, with no problems. The lab still shares one account, so the lab cannot repeat this.
2. **Keep the suite green.** Run `scripts/lab-suite.ps1 -Quick` after changes, and `scripts/mutants.ps1` after changing a guarded fix.
3. **Run tests on a quiet PC.** Other sessions' game labs (MiSide, Baldi) running at the same time push CPU to about 95%. The late lab copy (c3) then misses the run start, which fails tours that pass on a quiet PC.
4. **Not verified in play:** the five 0.2.9 fixes are covered by lab tours, some of them synthetic:
   - `pairing` spawns the client's own copy on purpose;
   - `puzzles` uses a test puzzle segment;
   - `lateending` simulates a late or lost ending;
   - the kept-player retargeting is watched end to end for Specimen 8 only (`chase`); Spec6, Hooked Doll, Frenzy and Nightmare are checked by counting rewritten reads.
5. **Steam lobby membership (reviewed 2026-10-27, left open on purpose):** the host accepts any Steam P2P connection, not only lobby members. A strict check needs a grace period (the member list and the connection request arrive by different paths) and a two-account test; see MODLOG 0.2.11.
6. **Window title on MelonLoader copies:** the launcher hides MelonLoader's console and names only the Unity window; if a copy shows "SpookyHD", its window appeared after 15 s.
7. **EMKHMonster5's update error at a room change** is listed as a known game error (it shows on the host too) without a host-only control run. Confirm it in single-player, or guard it, if it shows up in real play.

## Safety
- **Lab copies:** `private/lab-{host,client,c2,c3}` use hardlinks and junctions, so never edit game bytes through them.
- **Processes:** stop only the PIDs in `private/lab-pids.txt`.
- **Distribution:** never distribute `private/`, `deps/` or game files.
