# K2-D2 (Redux port)

An astromech-style autopilot suite for Kerbal Space Program 2, ported to the **Redux** modding framework from [Christophe Floutier's original K2-D2](https://github.com/cfloutier) (built for SpaceWarp1).

K2-D2 gives you one panel (`Alt-O` or the AppBar icon) with a set of autopilots:

- **Node** - executes the next maneuver node, with auto-circularize at Ap/Pe
- **Lift** - automated ascent guidance with a configurable altitude/heading profile
- **Landing** - automated descent, braking, and touchdown
- **Docking** - automated final approach and docking
- **Attitude** - point-and-hold attitude control (a simple plane autopilot)

## Status

This is a from-scratch port of the original code onto Redux's APIs, not a compatibility shim - it required chasing down and fixing a number of real differences between the old SpaceWarp1/UitkForKsp2 environment and Redux (custom UI controls, orbit representation, and more). The full blow-by-blow of everything found and fixed is in [`NOTICE.md`](NOTICE.md), kept as a development log for anyone porting a similar mod and hitting the same walls.

**Confirmed working (tested in-game):**
- Full UI - all tabs, styling, custom controls
- Node autopilot
- Lift/ascent autopilot - flies the configured profile to orbit; ends by asking for a manual circularization node (see Known limitations below)
- Landing autopilot - descent, braking, and touchdown, including collision detection
- Docking autopilot - final approach and main-thrust kill-speed/brake
- Attitude hold
- Auto-staging, with a player-facing on/off toggle in the window's title bar

**Known limitations:**
- The Lift/ascent autopilot asks for a manual circularization node rather than creating one itself. Wiring this up to K2-D2's native maneuver-node creation (rather than Flight Plan, which this port intentionally does not integrate) turned out to need more than a quick fix - see `NOTICE.md` for what was found.

## Installation

1. Install [Redux](https://ksp2redux.org) for Kerbal Space Program 2.
2. Download the latest K2-D2 release and drop the contents into your KSP2 `mods` folder (merge folders if prompted).

## Building from source

This is a Unity project using [ThunderKit](https://github.com/PassivePicasso/ThunderKit) and the [KSP2Community Redux.Template](https://github.com/KSP2Community/Redux.Template). Open the project in Unity, then run the `Rebuild and Launch` pipeline (Assets root) to build and deploy for local testing.

## Credits

- **[Christophe Floutier](https://github.com/cfloutier)** - original K2-D2 mod for SpaceWarp1
- **IanMealworm** - Redux port
- **[Mole](https://github.com/Mole1803)** - original Circularize work
- **[schlosrat](https://forum.kerbalspaceprogram.com/index.php?/profile/141963-schlosrat/)** - original testing and code help, especially node creation
- **Opus** - named the mod
- **[cheese3660](https://github.com/cheese3660)** - [SpaceWarp](https://github.com/Halbann) and [AutoBurn](https://github.com/cheese3660/AutoBurn), which the original mod was built on
- **[Halbann](https://github.com/Halbann)** - [LazyOrbit](https://github.com/Halbann/LazyOrbit), which the original mod's first steps were based on
- **[KSP2Community](https://github.com/KSP2Community)** - the Redux modding framework and the `Redux.Template` project scaffold this port is built on

## License

CC BY-SA 4.0 - see [`LICENSE.md`](LICENSE.md). Same license as the original K2-D2, inherited as required by its ShareAlike terms.
