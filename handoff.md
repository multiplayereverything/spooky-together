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

## State: 0.2.8, protocol 8
- **Source:** https://github.com/multiplayereverything/spooky-together (private, `main`). Commit as `multiplayereverything` with the GitHub no-reply address, and push after each version.
- **Moved 2026-10-25:** the source moved from `<game>/MultiplayerMod` to `ai-work-pc-games/Spooky Mulitplayer Mod`. `build.ps1` finds the game itself; the lab copies (`private/lab-*`) still hardlink and junction into `<game>`.
- **Online:** Steam lobbies plus Steam Networking Sockets P2P (`SteamLobby.cs`, `SteamNet.cs`). The global relay, room codes and mods-info are gone. LAN / direct (TCP 27020) and the LAN list (UDP 27021) remain.
- **Tests:** quick suite 11/11 (4 players), `state` and `enemies` over Steam, mutation check 0 of 6 survived (`MODLOG.md`). The full suite has not run on 0.2.8.
- **Installed:** BepInEx build in `<game>/BepInEx/plugins/SpookyTogether/` (SHA256 274cf353…bc5f), with BepInEx 5.4.23.5 restored from `deps/`. Not launched by hand yet.

## Next steps
1. **Real Steam session between two Steam accounts.** The lab shares one account, so Valve's P2P relay itself is untested. Check:
   - the lobby appears in a friend's list;
   - invite and Join Game work;
   - a full run reaches an ending.
2. **Keep the suite green.** Run `scripts/lab-suite.ps1 -Quick` after changes, and `scripts/mutants.ps1` after changing a guarded fix.
3. **Run tests on a quiet PC.** Other sessions' game labs (MiSide, Baldi) running at the same time push CPU to about 95%. The late lab copy (c3) then misses the run start, which fails tours that pass on a quiet PC.
4. **Open audit items, not fixed:**
   - Spec6, Spec8, HookedDoll, Frenzy and Nightmare cache the player before measuring distance, so the retarget transpiler misses them.
   - A client's own spawn arriving after the host's on-demand stand-in is removed after 6 s, and room scripts holding it lose it.
   - Two players using the same furnace or `TakeKey` at once can lose an update or duplicate a key.
   - `PropRefresh` re-runs puzzle condition events on every machine.
   - `ClientEnding` returns early when the client is no longer in a run.

## Safety
- **Lab copies:** `private/lab-{host,client,c2,c3}` use hardlinks and junctions, so never edit game bytes through them.
- **Processes:** stop only the PIDs in `private/lab-pids.txt`.
- **Distribution:** never distribute `private/`, `deps/` or game files.
