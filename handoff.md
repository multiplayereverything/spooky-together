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

## State: 0.2.9, protocol 9
- **Source:** https://github.com/multiplayereverything/spooky-together (private, `main`). Commit as `multiplayereverything` with the GitHub no-reply address, and push after each version.
- **Moved 2026-10-25:** the source moved from `<game>/MultiplayerMod` to `ai-work-pc-games/Spooky Mulitplayer Mod`. `build.ps1` finds the game itself; the lab copies (`private/lab-*`) still hardlink and junction into `<game>`.
- **Online:** Steam lobbies plus Steam Networking Sockets P2P (`SteamLobby.cs`, `SteamNet.cs`). The global relay, room codes and mods-info are gone. LAN / direct (TCP 27020) and the LAN list (UDP 27021) remain.
- **Tests (0.2.9):** quick suite 15 tours, all passing (two after load reruns); 11 mutation switches, none survived; `state` and `enemies` over Steam ran on 0.2.8 (`MODLOG.md`). The full suite has not run since 0.2.2.
- **Releases:** every version is a tagged GitHub release, pre-release while untested (the Discord bot announces them). v0.2.8 and v0.2.9 are not published yet: waiting for the user's OK.
- **Installed:** 0.2.9 BepInEx build in `<game>/BepInEx/plugins/SpookyTogether/` (SHA256 d033dcdf…d33b, the build the last tours ran), with BepInEx 5.4.23.5. Not launched by hand yet.

## Next steps
1. **Real Steam session between two Steam accounts.** The lab shares one account, so Valve's P2P relay itself is untested. Check:
   - the lobby appears in a friend's list;
   - invite and Join Game work;
   - a full run reaches an ending.
2. **Keep the suite green.** Run `scripts/lab-suite.ps1 -Quick` after changes, and `scripts/mutants.ps1` after changing a guarded fix.
3. **Run tests on a quiet PC.** Other sessions' game labs (MiSide, Baldi) running at the same time push CPU to about 95%. The late lab copy (c3) then misses the run start, which fails tours that pass on a quiet PC.
4. **Not verified in play:** the five 0.2.9 fixes are covered by lab tours, some of them synthetic:
   - `pairing` spawns the client's own copy on purpose;
   - `puzzles` uses a test puzzle segment;
   - `lateending` simulates a late or lost ending;
   - the kept-player retargeting is checked by counting rewritten reads, not by watching Spec6 or Spec8 chase a teammate.

## Safety
- **Lab copies:** `private/lab-{host,client,c2,c3}` use hardlinks and junctions, so never edit game bytes through them.
- **Processes:** stop only the PIDs in `private/lab-pids.txt`.
- **Distribution:** never distribute `private/`, `deps/` or game files.
