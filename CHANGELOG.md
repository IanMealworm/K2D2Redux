# Changelog

## 1.4.0

### Added

- Lift: new Gravity Turn ascent profile (Experimental), picked from the Ascent Profile dropdown next to Guided. It rises straight up, kicks over and follows prograde (orbital prograde once the air is thin), with the throttle holding the time to apoapsis until the target apoapsis is reached, then coasts out of the air and circularizes. Auto Kick & Hold (on by default) measures the rocket's drag on the way up and picks the kick angle and hold time from a quick prediction that includes the rocket's stages; their sliders only show when it's off. The throttle is limited by Max TWR and Max Dynamic Pressure, never drops below Min Throttle (so the rocket doesn't level off in the air), and eases off near the target apoapsis. The nose never goes below prograde; it comes up when the throttle can't climb as fast as the hold wants (so heavy, low-TWR rockets don't flatten out in the air), when a part nears its temperature limit while still in the air, and when the rocket is about to fall back. It's labelled Experimental while it's tuned on more rockets: unusual designs may need Auto Kick & Hold off and a gentler kick.
- Landing: the impact point and time come from Redux's own impact prediction when it's available (with Debug Mode on, the Prediction row shows Redux or K2D2).
- Lift and Landing: a hint to open Map View when a new maneuver node's map preview is waiting on it (the burn itself isn't affected).
- Landing: boost-back precision landing for boosters on atmospheric bodies. The boost-back burn re-aims several times a second until the predicted landing point is on target.
- Landing: new Flip Style setting for the boost-back: a powered flip (Super Heavy style) or an unpowered flip followed by the burn (Falcon 9 style).
- Landing: during the descent the booster steers with body lift toward the landing site.
- Landing: a single drag-aware landing (suicide) burn that steers onto the target, then a gentle final descent that cancels sideways drift and keeps the booster upright.
- Landing: the autopilot lowers the landing legs (the Gear action group) during the final part of the landing.
- UI: tooltips now appear in game when you rest the pointer on a setting, and every Advanced setting has one.

### Changed

- Lift: the Guided-only settings (turn altitudes, roll program) and the ascent graph only show while the Guided profile is selected.
- Landing (precision, no atmosphere): new powered descent that plans the whole braking burn onto a point above the target and re-plans it continuously, replacing the old tilt-to-steer braking. The Max Steering/Extend/Shorten Angle and RCS fine-correction settings are gone.
- Landing: each landing profile has its own waypoint picker, and the target follows the chosen waypoint if you move it in Redux's Waypoints window.
- Updated for Redux 26w41a and its Unity version.
- Landing: precision landing from orbit on atmospheric bodies is turned off for now (the panel says so); boost-back is the supported atmospheric precision landing. Entry Steepness is hidden until it returns.
- UI: stylesheets load once in a fixed order, so fonts and styles look the same on every tab.
- UI: one font throughout (JetBrains Mono), including buttons, fields, dropdowns and the compass; titles and headers in bold.
- UI: consistent text sizes across all tabs.
- UI: every tab uses the same section headers (fixes lower-case "tuning" and the line break in "Automatic Mode").
- UI: all buttons share one style and height.
- UI: slider values use sensible formats and units (10% instead of 0.10); Max Throttle and Start TouchDown Altitude now show their value.
- Lift: Pause Leaving Atmosphere is now off by default.
- Lift: the info panel shows only what matters (phase, apoapsis, time to apoapsis, throttle, kick and hold). The tuning details (altitude, angle of attack, TWR, dynamic pressure, heat, heading and roll) show when Debug Mode is on in the About tab.
- Landing: the info panel shows only what matters (altitude, fall speed, time to impact, when the burn starts, distance to the target, and what's left of a burn). The tuning details (predicted landing point and source, impact speed, burn length, attitude error, entry lift, throttle and more) show when Debug Mode is on in the About tab.

### Fixed

- Landing: precision deorbit and mid-course burns account for the body turning underneath during the fall.
- Landing: precision landing won't start from a trajectory that's already coming down (it used to plan its circularize burn after impact and throw an error).
- No more errors when engines are destroyed in a crash, or during game startup before the game is ready.
- Dropdowns are a normal single-line height instead of stretching to fill the panel.
- Landing: touchdown is detected once the vessel is standing still, even when the game's ground height reading is off.
- Landing: impact prediction no longer throws an error when the prediction has no result.
- Lift: "Circularize once complete" now works. When it's off, the ascent ends after the final apoapsis adjustment so you can plan your own circularization.
- Lift: "Pause when leaving atmosphere" now pauses at the edge of the atmosphere and ends the ascent without touching your maneuver nodes. It's greyed out while auto-circularize is on.
- Lift: time warp while coasting to the edge of the atmosphere now picks the right rate.
- Lift: with Coasting Warp off, only the warp is skipped. The coast still runs and drag losses are still made up before circularizing.
- Lift: the ascent profile is locked once a launch starts; changing the dropdown mid-flight no longer disrupts the run.
- Lift: the roll program no longer leaves SAS locked after the ascent ends or is stopped.
- Lift: circularization stops with a message if the maneuver node can't be created, instead of waiting forever.
- Lift: Reset refreshes the profile dropdown; End Ascent and Final Adjust labels follow the target apoapsis; Max Throttle shows its value and can't be set to 0.
- Dock: no more error spam after the target is cleared while aligning to the dock.
- Dock: main-thrust kill speed now throttles correctly instead of always burning at full throttle.
- Settings with limits are now kept in range: a negative warp-before-burn no longer warps past the burn, Max Angle 0 no longer stalls the Node pilot, and Attitude elevation stays within ±90°.
- Dock: starting docking stops the other pilots, and Stop releases RCS translation. A leftover align-to-dock no longer steers SAS while the Dock tab is open.
- Landing: correct height reading on the launch pad and near the space center, and touchdown is detected by standing still in every landing mode.
- Landing: the Precision toggle is locked during a run (turning it off mid-entry could start a second boost-back).
- Landing: the Touch Down button starts touchdown directly instead of running the precision setup first.
- Landing: the collision readout no longer flickers, and the vacuum plan is no longer cleared between predictions.
- Landing: the touchdown fallback no longer interrupts the atmospheric descent or the boost-back.
- Landing: the Safe Warp Rotation slider now takes effect.
- UI: focus and invalid-input highlight colours now show.
- UI: consistent units and wording, typo fixes ("Fligh View", "(dockin)", "thust") and corrected tooltips.
- UI: the Lift profile and Landing waypoint dropdowns fit the window.
- Node: warp settings stay hidden at load when Auto Warp is off.
- Lift: heading correction ignores noise at low horizontal speed (Gravity Turn and Guided).
- Landing (non-precision) on atmospheric bodies: the landing burn uses the drag-aware planner and starts at the right time, instead of far too early with repeated brake/pause cycles.
- Node: the circularize buttons no longer delete the rest of your flight plan.
- Node: status is correct when a start fails, the maneuver solver isn't polled while idle, and the undocumented Ctrl+O reset-all hotkey is gone.
- Auto-staging: no longer stages everything (parachutes included) when an empty engine stays attached, and no longer gets stuck on a tank with 0 capacity.
- About → Reset All also resets the Landing profiles; Attitude settings are saved under their own names.
- Times show tenths of a second correctly, a Node burn with no active engine says so instead of showing "Infinity s", and Dock's Manual mode shows the right Vertical and Depth speeds.
- Landing: the waypoint list no longer overwrites a target you typed in.
- Landing: the vacuum Precision toggle is no longer switched off by itself (it was, in the atmosphere and during transfers). When the orbit is too high to start from, a note under it says so and Start refuses.
- Landing: a hand-made deorbit on an atmospheric body no longer reaches the disabled from-orbit mode, and Max Plane Trim is hidden on the atmospheric panel.
- Landing: Stop releases the SAS target and removes the landing pilot's Circularize, Deorbit and Mid-Course nodes.
- Landing: airless-only readouts (Collision Speed, Start Burn In, Burn Duration) are hidden during atmospheric precision landing.
- Window: only the left mouse button drags or resizes it (a right-drag to turn the camera also moved the window).
- Window: clicking it without dragging no longer saves its position as the top-left corner.
- Window: it stays fully on screen at any resolution or UI scale, including when resized after being moved. Thanks to LeoMarinDev for finding these window bugs.
- K2-D2 no longer spams the log every frame if it fails to start.

### Performance

- Info panels on every tab update their text in place instead of being rebuilt every frame.
- Landing: the ground-collision search runs only when needed and a few times a second (it made up to 300 terrain queries per frame).
- Landing: vacuum precision re-plans a few times a second while coasting instead of every frame.
- Lift: the path graph isn't redrawn while hidden, and nothing heavy runs on the pad.
- Tabs stop refreshing while the K2-D2 window is closed; running pilots carry on as normal (thanks to LeoMarinDev).
- Telemetry is looked up once instead of by reflection on every call.
- Less log spam: collision and comparison logs only while a pilot is running or in debug.
- Much smaller download (about 92 MB down to about 6 MB): the old unused UI bundles and the unused Caravan font (with the large fallback fonts it pulled in) are gone. Thanks to LeoMarinDev for finding this.

### Removed

- Removed unused code: the old IMGUI target picker, an unused touchdown controller, unused vessel and reflection helpers.
- UI: removed unused elements, including the unwired buttons under Lift.

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
