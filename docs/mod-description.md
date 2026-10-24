Spooky Together

Play through the mansion with up to 4 players. Everyone sees the same rooms, gets chased by the same specimens, shares the same keys and items, and reaches the endings together.

Comes as one ready-to-install download with BepInEx included. Just extract it into the game folder.

Features
Shared Levels

* Everyone explores the same rooms with synchronized layouts, lighting, locked doors, and decorations.
* Opening an exit door moves the whole team forward. Elevators, floor transitions, trams, and scripted sequences bring everyone along.
* Supports the base mansion, Endless mode, the Hospital (Karamari), and the Dollhouse.

Shared Specimens

* The host controls every specimen. All players see synchronized positions, animations, and sounds.
* Specimens chase the nearest standing player and attack their target.
* Specimen 1's cut-outs appear on the same side with the same image for everyone.

Shared Items and Story

* Keys, notes, puzzle progress, and inventory are shared across the team.
* Picked-up items disappear for everyone, and unlocked doors unlock for everyone.
* The host checks player states every 10 seconds and repairs discrepancies.

The Axe and Other Weapons

* Picking up a weapon grants it to the whole team. Teammates can see you holding and swinging it.
* Boarded doors, webs, and breakable walls synchronize for everyone.
* Any player can hit the Taker or reflect its fireball.

Endings

* Triggering an ending or credits sequence brings the whole team along.
* Your choices still determine whether you get the good or bad ending.

Downed, Not Dead

* If a teammate is still standing, a lethal hit knocks you down instead of ending the run.
* While downed, you can spectate teammates until the next room, where you get back up.
* The run only ends when everyone is down.

Quality of Life

* Server browser with automatic local-network discovery and direct IP joining.
* Coloured player figures with name tags.
* The game won't pause when you tab out while connected.
* Players can join a run already in progress.
* Host-only debug menu on F6: teleport to rooms, spawn or clear specimens, enable god mode, and save bug reports.

Installation
All players need the same game version and the same mod version (0.2.6).

1. Download `SpookyTogether-0.2.6-Ready.zip`.
2. Open the game folder (the one containing `SpookyUnity.exe`).

   * Steam: Right-click the game → Manage → Browse local files.
3. Extract everything in the ZIP into that folder. If asked, replace the existing files.
4. Launch the game through Steam, then press F5 to open the multiplayer panel.

The ZIP includes BepInEx 5.4.23.5 already set up, the mod, and a HOW TO INSTALL.txt. Nothing else needs installing.

To uninstall, delete `winhttp.dll` from the game folder (or delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and the `BepInEx` folder to remove everything).

How to Play

1. Start the game and press F5 to open the multiplayer panel. Set your name and colour.
2. One player presses Host.
3. Everyone else selects the host from the server list or enters the host's IP address and presses Join.
4. The host starts a run from the main menu using New Game or Continue. Everyone is automatically brought into the mansion.

Playing Over the Internet

* The host needs TCP port 27020 reachable. Forward the port, or use a VPN such as Tailscale, ZeroTier, or Radmin VPN and join using the host's VPN IP.
* The server list discovers games on the same local network using UDP port 27021.

Controls

* F5 — Multiplayer panel.
* F6 — Debug menu (host only).
* Click while downed — Switch the teammate you're watching.

Known Limitations

* The Taker boss attacks each player separately. The host determines its health and the ending.
* Health, stamina, flashlight state, and on-screen notes are per player.
* Some puzzle props only update after re-entering the room.
* The AFK Taker (Specimen 9) only appears for the player who idles.
* There is no host migration. If the host leaves, other players continue alone.
* Launch through Steam. Starting the executable directly causes the game to restart through Steam.

Bug Reports
If something goes wrong, the host can press F6 → Save report and send the generated file from `UserData/SpookyTogether/` in the game folder.

Please also include each player's `BepInEx/LogOutput.log` from the game folder and their `Player.log`, located at:

`%USERPROFILE%\AppData\LocalLow\Albino Moose Games\SpookyHD\`

Credits

* Mod by LarpingModder.
* Spooky's Jump Scare Mansion: HD Renovation belongs to its respective developers. This is an unofficial fan mod, not affiliated with or endorsed by them.
* Uses BepInEx and Harmony.
