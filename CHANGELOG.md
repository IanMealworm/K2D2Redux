# Changelog

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
