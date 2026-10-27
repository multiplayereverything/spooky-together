# Spooky Together handoff

Updated 2026-10-26. Paths are relative to this folder (the mod's git repo). The game is in Steam's `steamapps/common/Spooky's Jump Scare Mansion HD Renovation` (`<game>` below).

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

## Paused 2026-10-26 (user: "stop the mods for now")
- **0.2.10 is published as a pre-release with a known issue** (2026-10-27, at the user's request, with no further fixes or tests). It is committed, pushed and installed (BepInEx; the 0.2.9 plugin is backed up in `private/backups/install-0.2.10`). The known issue is the `takerbail` failure below.
- **Last results on the 0.2.10 code.**
  - Full suite (before the later fixes): 22/27. The fails were takerbail, pairing, chase, latelook and EM_Mansion.
  - Reruns after the fixes: pairing, chase, latelook and EM_Mansion pass. `loadending` passes twice.
  - **`takerbail` still fails.** Clients get Credits as the host bails to it, "while this room loads". They never start loading End_05, so the held ending is dropped after 30 s (logs: `artifacts/tests/logs/takerbail`).
  - The `endingload` and `pairing` mutation reruns were not run.
- **Resume with:**
  1. Fix the ending hold so it cannot stall. `RoomLoading` is true here, but no room load follows; check whether `pendingRoom` was set before the ending arrived. Either apply the ending once no load has started within a few seconds, or treat "loading" as an active `HSMLoadRoomState` transition. Rerun `takerbail` and `loadending`.
  2. Rerun the mutants for `endingload`, `pairing`, `earlypuppet`, `lookretry` and `keyorder`. Then run the full suite.
  3. Then release the fix as 0.2.11: pre-release, Website session (local_211a3291-3165-4fdf-8d32-3da6780cf595).

## State: 0.2.10 (in progress), protocol 9
- **Source:** https://github.com/multiplayereverything/spooky-together (private, `main`). Commit as `multiplayereverything` with the GitHub no-reply address, and push after each version.
- **Moved 2026-10-25:** the source moved from `<game>/MultiplayerMod` to `ai-work-pc-games/Spooky Mulitplayer Mod`. `build.ps1` finds the game itself; the lab copies (`private/lab-*`) still hardlink and junction into `<game>`.
- **Online:** Steam lobbies plus Steam Networking Sockets P2P (`SteamLobby.cs`, `SteamNet.cs`). The global relay, room codes and mods-info are gone. LAN / direct (TCP 27020) and the LAN list (UDP 27021) remain.
- **Tests (0.2.10):** the full suite ran on 0.2.9 (21/24; the two real failures are fixed in 0.2.10, see `MODLOG.md`). New tours `chase`, `latekey`, `latelook`; 14 mutation switches, none survived. `state` and `enemies` over Steam ran on 0.2.8.
- **Releases:** every version is a tagged GitHub release, pre-release while untested (the Discord bot announces them). v0.2.8 and v0.2.9 are published as pre-releases with their ZIPs (2026-10-26). `scripts/package.ps1` builds the ZIPs; lint them with `um publish check` before uploading.
- **Installed:** 0.2.9 BepInEx build in `<game>/BepInEx/plugins/SpookyTogether/` (SHA256 d033dcdf…d33b, the build the last tours ran), with BepInEx 5.4.23.5. Not launched by hand yet.

## Next steps
1. **Real play (done, reported 2026-10-27):** the owner played with three or more friends on separate PCs and separate Steam accounts, four players together, with no problems. The lab still shares one account, so the lab cannot repeat this.
2. **Keep the suite green.** Run `scripts/lab-suite.ps1 -Quick` after changes, and `scripts/mutants.ps1` after changing a guarded fix.
3. **Run tests on a quiet PC.** Other sessions' game labs (MiSide, Baldi) running at the same time push CPU to about 95%. The late lab copy (c3) then misses the run start, which fails tours that pass on a quiet PC.
4. **Not verified in play:** the five 0.2.9 fixes are covered by lab tours, some of them synthetic:
   - `pairing` spawns the client's own copy on purpose;
   - `puzzles` uses a test puzzle segment;
   - `lateending` simulates a late or lost ending;
   - the kept-player retargeting is watched end to end for Specimen 8 only (`chase`); Spec6, Hooked Doll, Frenzy and Nightmare are checked by counting rewritten reads.
5. **EMKHMonster5's update error at a room change** is listed as a known game error (it shows on the host too) without a host-only control run. Confirm it in single-player, or guard it, if it shows up in real play.

## Safety
- **Lab copies:** `private/lab-{host,client,c2,c3}` use hardlinks and junctions, so never edit game bytes through them.
- **Processes:** stop only the PIDs in `private/lab-pids.txt`.
- **Distribution:** never distribute `private/`, `deps/` or game files.
