# Changelog

All notable changes to CK3 x Bannerlord. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
versions follow [Semantic Versioning](https://semver.org/).

## [0.9.0-beta] - 2026-10-08

The first public release. Built for Crusader Kings III 1.20 and Mount & Blade II: Bannerlord v1.4.8 (Steam), on Windows 10
and 11.

### Added

- **Battles in Bannerlord.** Field battles you take part in, or that belong to your war, can be fought in Bannerlord. CK3
  pauses itself and asks; "Let my commanders decide the day" keeps CK3's own resolution.
- **Your armies and your people.** Levies and men-at-arms by type and culture, scaled to Bannerlord's battle size; you as your
  own character (or as your army's commander when you are away); commanders and knights of both sides as named heroes; faces
  rebuilt from your save; coats of arms on the banners; a battle map chosen for the CK3 terrain, climate and season.
- **A seamless switch.** Bannerlord waits preloaded, hidden and muted. A freeze frame of CK3 turns into a title card that
  dissolves into the battle; a result card brings you back.
- **Results back in CK3.** Losses per army and men-at-arms type, wounds, maimings, deaths and captures by CK3's own odds,
  prestige for your own kills, and prestige, glory and prowess for heroes who did well.
- **The battle clock.** CK3's own combat fights the result out over about 12 days, so war score, the battle summary and the
  aftermath are native. A second Bannerlord clash is offered when the loser is reinforced.
- **Knights and commanders.** Knight effectiveness, commander traits, accolades, artifacts and personal traits shape the
  heroes, their retinues and their side, within caps.
- **Achievement-friendly Normal mode.** Results enter CK3 through the mod's hidden buttons on CK3's modder hotkeys; the console
  and debug mode are never used. A Dev mode with the console is available for modders.
- **Tray app.** Launch CK3 in either mode, see the battle and Bannerlord state, abort hotkey Ctrl+Alt+Shift+End, Set game
  folders, Start with Windows, About with install checks and "Copy diagnostics".
- **Installer.** One self-contained setup exe, per user, no administrator rights, no .NET install needed; detects Steam, GOG and
  Epic installs; upgrade, repair and a clean uninstall. A manual zip for those who prefer copying files.
- **Documentation** for players, modders and contributors.

### Known issues

- The installer, the installed layout, and GOG and Epic detection are new and tested with automated tests only.
- Fixes made after the last play test still need an in-game check: playing as yourself when no army of your side has a
  commander, the wording of a second clash, war score when a leaderless loser is steered, bridge start-up, and leaner battle
  clock logging.
- Most knight and commander effects are tested in code but not yet in game.
- Achievements were checked in game on CK3 1.20.0.3; a re-check on 1.20.0.4 is pending.
- The exe is not code-signed: SmartScreen and some antivirus programs warn, and Windows 11's Smart App Control, when it is
  on, blocks the installer and the programs it installs with no override.
- Bannerlord must be started once by hand after installing, so it can show its first-start notices.
- Banner charges are the nearest Bannerlord icon of the right kind, not exact copies.
- The mod's texts are English only.

[0.9.0-beta]: https://github.com/graff0n/ck3-bannerlord-bridge/releases/tag/v0.9.0-beta
