# Changelog

## 1.1 — Email crash and confirmation fix

- Uses the game's built-in acknowledgement email content for streamer announcements, with the standard confirmation action.
- Repairs older streamer announcement emails that have no content before opening them.
- Preserves the existing role selection, reserve movement, PR review, localization, and notice suppression behavior.

## 1.0 — First public version

- Hotfix: resolves the managed club through `GlobalValues.DataTeam`, moves selected players out of `MainSquad`, filters active reserve streamers from the talk-dialog candidate picker, and gives streamer announcement emails the game's standard confirm action.
- Adds **Streamer** as the third option in the main-role dropdown.
- Moves a player from the Main Squad to the reserve squad when Streamer is selected.
- Changes the role label beside the stars to Streamer while active and restores the player's normal role when AWPer or Rifler is selected.
- Keeps the player's underlying AWPer/Rifler role and match attributes unchanged.
- Packages the plugin under `BepInEx/plugins/` for straightforward archive extraction.

## 0.1.1 — Role menu and installation update

- Adds the Streamer option using the game's TMP_Dropdown API directly.
- Logs each installed role-menu patch and confirms when the option is added for a player's profile.
- Packages the plugin under `BepInEx/plugins/` so the archive can be extracted into the game folder.
- Targets EM26 ModKit 2.3.0 interop assemblies.

## 0.1.0 — Initial development build

- Adds a manually selected full-time Streamer / match-host role to a reserve player's profile role dropdown. The mod never assigns the role automatically.
- Adds a +1 PR reward for the player and club every six in-game months, after a 14-day settling-in period.
- Adds a 0.3% default viral-story chance (configurable from 0.1% to 0.5%), a modest extra PR gain, and a localized in-game email.
- Supports the language codes present in the installed game build, with English fallback.
- Suppresses bench-sitting notifications and talk prompts while the reserve is an active streamer.
- Adds configurable normal PR, viral PR, and viral chance values.

The role menu and gameplay behavior still require in-game verification before this can be called a verified release.
