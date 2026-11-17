# Bench Streamer

**A small career mod for Esports Manager 2026.** Adds **Streamer** as the third choice in the player's main-role dropdown. Choosing it moves the player from the Main Squad to the reserve squad. Their AWPer/Rifler gameplay role is preserved underneath the mod role.

## Planned features

- Open any player on your club's profile and choose **Streamer** beneath **AWPer** and **Rifler** in the main-role dropdown. The mod moves that player to the reserve squad and assigns the Streamer activity. Choose AWPer or Rifler again to return them to a normal player role. Assignment is manual; the mod never picks a player automatically.
- While the selected player is outside the Main Squad, they act as the club's full-time streamer and match host.
- On the next in-game day after assignment, the manager receives a one-time email announcing that the player has started streaming. The email follows the game's selected language.
- After a 14-day settling-in period, the player and club each gain +1 PR after every six in-game monthly reviews.
- At each six-month review, a viral story has a 0.3% default chance (configurable from 0.1% to 0.5%). A viral story gives +2 PR total to both the player and club and sends the manager an in-game email.
- Viral emails use the active game language. English and Turkish are supported, with translations for the other language codes found in the game; unknown codes fall back to English.
- Streaming activity and bench-notice suppression apply only while the selected player is on the club roster and outside the Main Squad.
- Active reserve streamers are removed from the game's bench-sitting talk-dialog candidate selection; other reserve players remain eligible for their normal messages.

## Important game limitation

The game does not define a `Streamer` player or staff role. Bench Streamer adds a third main-role dropdown choice and stores that selection in its config. Choosing Streamer moves a Main Squad player to the reserve squad. The player's underlying AWPer/Rifler gameplay role is preserved, so match simulation attributes stay intact.

## Requirements

- Esports Manager 2026 for Windows (Steam)
- EM26 ModKit / BepInEx 6 IL2CPP

The mod package must contain only Bench Streamer's own plugin and documentation. It must not bundle the game, the ModKit, or another author's files.

PR amounts and the viral chance can be changed in the generated BepInEx config file. Streamer assignments and six-month progress are saved there as well.

## Installation

1. Install EM26 ModKit 2.3.0 (BepInEx 6 IL2CPP) and launch the game once.
2. Close the game.
3. Extract the downloaded archive into the game's root folder (the folder containing `Esports Manager 2026.exe`) and allow it to merge the `BepInEx` folder.
4. Confirm the file exists at `Esports Manager 2026/BepInEx/plugins/BenchStreamer.dll`.
5. Start the game and open a player on your club. The main-role dropdown should show **Streamer** as its third option.
6. If it is missing, check `BepInEx/LogOutput.log` for `Bench Streamer 1.1`, `PlayerOverview.Init ran`, and `Streamer role option ready`.

To update from an older version, close the game first, extract the new archive into the same game folder, and replace the existing DLL when prompted. Do not leave two Bench Streamer DLL copies in `BepInEx/plugins`.

## Compatibility and saves

This is a single-player career plugin. It should not be used in shared or multiplayer careers. Back up important saves before installing gameplay mods. Compatibility must be confirmed against the game and ModKit builds listed on the release page.

## Development status

Version 1.1 is built against the local EM26 ModKit 2.3.0 interop assemblies. It uses the game's built-in acknowledgement content for streamer announcement emails so they have the normal confirmation action. Selecting Streamer moves the player out of the Main Squad, updates the role label, and schedules a localized start-streaming email for the next in-game day.

## Build from source

With the game and EM26 ModKit installed at the default Steam path, run `dotnet build BenchStreamer.csproj -c Release` from this folder. For a different game folder, pass `-p:GameDir="FULL_PATH_TO_GAME"`. The resulting plugin is written to `bin/Release/BenchStreamer.dll`.

