# Changelog

## K2-D2 v1.4.1 for Redux v0.2.9.0 Beta

**Changed:**
- Updated for Redux 26w42a. This version needs 26w42a or newer.
- K2-D2 only runs in campaigns that have it turned on.
- The window scales with the game's UI Scale setting.
- The window uses Redux's resize handle, and can now be made wider as well as taller.
- The info button matches K2-D2's other buttons.

**Fixed:**
- The game no longer hangs while loading on Redux 26w42a.

## K2-D2 v1.4.0 for Redux v0.2.9.0 Beta

**Added:**
- Ascent autopilot: new Gravity Turn profile (Experimental). It works out the turn for your rocket by itself.
- Ascent autopilot: now flies its own closed-loop circularization burn instead of following a maneuver node, so the orbit comes out round even when the launch ends far from orbit.
- Landing autopilot: boost-back landings for boosters on planets with air. It flies the booster back and lands it on target, with a choice of a powered or unpowered flip.
- Landing autopilot: lowers the landing legs near the ground.
- Landing autopilot: uses Redux's own landing prediction when it can.
- Tooltips: hold the mouse over a setting to see what it does.

**Changed:**
- Ascent autopilot: Guided profile settings only show when Guided is picked, and Pause Leaving Atmosphere is now off by default.
- Landing autopilot: more accurate precision landings on moons and planets without air.
- Landing autopilot: each landing profile has its own waypoint list, and the target follows the waypoint if you move it.
- Landing autopilot: precision landing from orbit on planets with air is off for now. Use boost-back instead.
- Tweaked the UI.
- Trimmed the info panel data. Extended data shows when Debug Mode is on.
- Much smaller download: about 92 MB down to 6 MB (thanks to LeoMarinDev).
- Updated for Redux 26w41a.

**Fixed:**
- Ascent autopilot: Circularize Once Ascent Complete and Pause Leaving Atmosphere now work.
- Ascent autopilot: time warp, the roll program and changing the profile mid-launch no longer cause problems.
- Landing autopilot: precision landings land closer to the target.
- Landing autopilot: touchdown is always noticed, and the landing burn no longer starts much too early on planets with air.
- Landing autopilot: the Precision toggle no longer turns itself off, a typed-in target is no longer replaced, and Stop cleans up its maneuver nodes.
- Node autopilot: the circularize buttons no longer delete future nodes.
- Docking autopilot: kill speed no longer always burns at full throttle, and clearing the target no longer causes errors.
- Auto-staging no longer stages everything when an empty engine stays attached.
- The window no longer moves when you right-drag the camera, jumps to the corner after a restart, or goes off screen (thanks to LeoMarinDev).
- Buttons no longer touch, long labels no longer run off the panel, and typos are fixed.
- No more errors during crashes or at game startup.

**Known Issues:**
- Gravity Turn is still experimental. Unusual rockets may need Auto Kick & Hold turned off and a gentler kick.

## 1.3.0

### Added

- Node autopilot: two new buttons to create a maneuver node at the next apoapsis or at the next periapsis.
- Ascent autopilot can now create and fly its own circularization node at the end of ascent, instead of requiring one to be built by hand.
- Ascent autopilot can fly a specified roll program during the climb.
- Landing autopilot can now perform precision landings on bodies with no atmosphere, targeting a specific site via Redux's own waypoint system. (An atmospheric version of precision landing exists in code but is still a work in progress and not yet exposed to players.)

### Changed

- Every autopilot now deletes the maneuver node it executes once the burn finishes, instead of leaving it sitting on the plan.
- Precision landing settings for bodies with and without an atmosphere are now two fully separate autopilot profiles, so tuning one can't affect the other. (The atmospheric profile is still work in progress and not player-facing yet.)
- Node's "Rotate During Burn" option (Node tab, Experimental section) now defaults to on, so SAS stays locked to the maneuver direction for the whole burn instead of dropping to Stability Assist partway through. Since every burn in the mod shares the same execution code, this improves accuracy for Node, Lift's circularize, and Landing's burns, not just Node's own. The toggle is still there for anyone who wants the old fixed-orientation behavior back.
- All custom UI controls migrated from the legacy `UxmlFactory`/`UxmlTraits` pattern to the modern `UxmlElement` attribute system.
