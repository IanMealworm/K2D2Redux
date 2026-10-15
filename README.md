# K2-D2 (Redux port)

An astromech-style autopilot suite for Kerbal Space Program 2, ported to the **Redux** modding framework from [Christophe Floutier's original K2-D2](https://github.com/cfloutier) (built for SpaceWarp1).

K2-D2 gives you one panel (`Alt-O` or the AppBar icon) with a set of autopilots:

- **Node** - executes the next maneuver node, with auto-circularize at Ap/Pe and one-click buttons to create a node at the next apoapsis or periapsis
- **Lift** - automated ascent to orbit, either along a configurable altitude/heading profile (Guided, with an optional roll program) or as a real gravity turn (Gravity Turn, Experimental), with automatic circularization at the end of the climb
- **Landing** - automated descent, braking, and touchdown, with a drag-aware landing burn on bodies with an atmosphere, and an optional Precision Landing mode that targets a site from Redux's waypoint system: a guided powered descent from orbit on bodies with no atmosphere, or a boost-back landing for boosters on bodies with one
- **Docking** - automated final approach and docking
- **Attitude** - point-and-hold attitude control (a simple plane autopilot)

## Status

This is a from-scratch port of the original code onto Redux's APIs, not a compatibility shim - it required chasing down and fixing a number of real differences between the old SpaceWarp1/UitkForKsp2 environment and Redux (custom UI controls, orbit representation, and more). The full blow-by-blow of everything found and fixed is in [`NOTICE.md`](NOTICE.md), kept as a development log for anyone porting a similar mod and hitting the same walls.

**Confirmed working (tested in-game):**
- Full UI - all tabs, styling, custom controls
- Node autopilot
- Lift/ascent autopilot - Guided profile (with optional roll program) and Gravity Turn profile to orbit, then flies its own closed-loop circularization burn
- Landing autopilot - descent, braking, and touchdown on bodies with and without an atmosphere, including collision detection, precision landing on bodies with no atmosphere, and boost-back precision landing for boosters
- Docking autopilot - final approach and main-thrust kill-speed/brake
- Attitude hold
- Auto-staging, with a player-facing on/off toggle in the window's title bar

**Known limitations:**
- The Gravity Turn ascent profile is Experimental: it flies most rockets well, but unusual designs may need Auto Kick & Hold turned off and a gentler kick.
- Atmospheric precision landing from orbit isn't available yet - on bodies with an atmosphere, precision landing currently lands stages that are already coming back down (boost-back landings).

## Installation

1. Install [Redux](https://ksp2redux.org) for Kerbal Space Program 2.
2. Make sure you're on KSP2 beta snapshot 26w41a or newer (Redux Launcher: Settings [gear icon to the right of the "Mods" tab] > Release Channel > Beta, then update to the latest beta).
3. Download the latest K2-D2 release and drop the contents into your KSP2 `mods` folder (merge folders if prompted).

## Credits

- **[Christophe Floutier](https://github.com/cfloutier)** - original K2-D2 mod for SpaceWarp1
- **IanMealworm** - Redux port
- **[Mole](https://github.com/Mole1803)** - original Circularize work
- **[schlosrat](https://forum.kerbalspaceprogram.com/index.php?/profile/141963-schlosrat/)** - original testing and code help, especially node creation
- **Opus** - named the mod
- **[cheese3660](https://github.com/cheese3660)** - [SpaceWarp](https://github.com/Halbann) and [AutoBurn](https://github.com/cheese3660/AutoBurn), which the original mod was built on
- **[Halbann](https://github.com/Halbann)** - [LazyOrbit](https://github.com/Halbann/LazyOrbit), which the original mod's first steps were based on
- **[KSP2Community](https://github.com/KSP2Community)** - the Redux modding framework and the `Redux.Template` project scaffold this port is built on
- **LeoMarinDev** - found what was making the download so large, and the window drag, resize and closed-window update bugs ([PR #2](https://github.com/IanMealworm/K2D2Redux/pull/2))
- **[linuxgurugamer](https://github.com/linuxgurugamer/GravityTurn)** - the KSP1 GravityTurn mod, whose approach informed K2-D2's Gravity Turn profile (no code was used)

## Changelog

See [`CHANGELOG.md`](CHANGELOG.md) for release notes.

## License

CC BY-SA 4.0 - see [`LICENSE.md`](LICENSE.md). Same license as the original K2-D2, inherited as required by its ShareAlike terms.
