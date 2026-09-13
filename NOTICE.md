# Attribution

K2D2 for KSP2 Redux is licensed under Creative Commons Attribution-ShareAlike
4.0 International (CC-BY-SA 4.0) - see `LICENSE.md`.

## K2-D2 (original)

This project is a port of **K2-D2** by cfloutier -
https://github.com/cfloutier/k2d2 - itself licensed under CC-BY-SA 4.0. Per
that license's Attribution and ShareAlike terms: this is Adapted Material,
K2-D2's own CC-BY-SA 4.0 license and copyright are acknowledged here, and this
project (including all adapted portions) is licensed onward under the same
CC-BY-SA 4.0 terms.

Per K2-D2's own README, credit for specific parts of the original mod is also
due to: Mole (Circularize work), schlosrat (testing, node-creation help -
also the author of Flight Plan, which K2-D2 optionally integrates with), Opus
(mod name), and cheese3660 (SpaceWarp itself, and AutoBurn - reference code
for starting thrusts / reading maneuver nodes). K2-D2's own first steps were
based on Halbann's LazyOrbit.

## KSP2Community/K2D2Redux (the initial Redux port)

This project builds directly on an existing, unfinished Redux port:
**KSP2Community/K2D2Redux** - https://github.com/KSP2Community/K2D2Redux -
also CC-BY-SA 4.0, explicitly described there as "a port of Cfloutier's K2-D2
to KSP2 Redux." Its "Basic port" commit (by Lexi, July 2025) already did real
work re-targeting the plugin entry point at Redux's current API surface
(`Redux.ExtraModTypes.KerbalMod`, `ReduxLib.Logging.ILogger`,
`SpaceWarp2.UI.API.Appbar`) rather than the original's SpaceWarp1/BepInEx
base, but was never built or tested against a live game, and its Flight Plan
integration (`K2D2OtherModsInterface.cs`) was left a commented-out stub. This
project starts from that port's source (`Assets/K2D2/Code`) rather than
porting fresh from the original, to build on the real work already there.

# Redux port notes

This section is a technical reference for what actually broke porting K2-D2
from SpaceWarp1/UitkForKsp2 to Redux, and how each issue was fixed - kept for
anyone porting a similar mod (or building on Redux's node/orbit APIs) and
likely to hit the same walls. It's organized by topic rather than
chronologically; every fix described here has been confirmed working in-game
unless a section says otherwise.

## Redux API differences from SpaceWarp1

- **`GlobalGameState.GetState()` doesn't exist** on the current
  `KSP.Game.GameStateMachine`. Use `GlobalGameState.GetGameState().GameState`
  instead. This gated K2D2's entire per-frame update pipeline, so it failed
  immediately on load.
- **`SpaceWarpPluginDescriptor.Folder` (`SWMetadata.Folder`) is a
  `System.IO.DirectoryInfo`, not a `string`.** String-concatenating it
  directly happens to work via `DirectoryInfo.ToString()`, but the correct
  accessor is `.Folder.FullName`.
- **`KSP.Api.CoreTypes.PropertyExternal<T>` uses `.GetValue()`, not
  `.Value`.** (An earlier verification pass in this project incorrectly
  "fixed" this to `.Value` based on an incomplete assembly scan - if you see
  that change referenced anywhere, it was wrong and reverted. `.GetValue()`
  is correct.)
- **`VesselComponent.Orbit` is typed `KSP.Sim.IKeplerPatch`, an interface -
  not the concrete `PatchedConicsOrbit` class.** Hard-casting it to
  `PatchedConicsOrbit` compiles and often works, but throws
  `InvalidCastException` specifically for the vessel that's actively being
  flown: Redux's ECS layer hands that vessel's orbit back as
  `Redux.Ecs.Components.CurrentPatchedConicsOrbit`, a completely unrelated
  class that also implements `IKeplerPatch`. **Fix:** don't cast to the
  concrete class - use interface members (`IOrbit`/`IKeplerPatch`) instead,
  wherever the API you need is available on the interface. This one bug
  pattern caused most of the runtime crashes found in this port (see Lift/
  Landing below).
- **`ManeuverNodeData.SetManeuverState()` only accepts the concrete
  `PatchedConicsOrbit` class - there's no interface-typed overload.** Unlike
  every other orbit-cast bug above, this one can't be fixed by widening to
  `IOrbit`/`IKeplerPatch`. Whether a real `PatchedConicsOrbit` instance is
  obtainable at all for the actively-flown vessel under Redux's ECS orbit
  model is still an open question - `ManeuverPlanSolver` separately exposes
  both a `PatchedConicsList` and a `PatchedNBodyList`, plus a
  `GetOrbitalElements()` method that looks like it might convert between the
  two, but this hasn't been worked out. **This is the concrete blocker for
  wiring up circularization/node creation for the live vessel without going
  through Flight Plan** - relevant if you're porting Node Manager/Flight
  Plan's own node-creation code.
- **`K2D2.asmdef` needed an explicit reference to `Unity.Entities`**, because
  `KSP.Sim.impl.UniverseModel` extends `Unity.Entities.SystemBase` directly.
  Add it as a normal assembly-definition `references` entry, not a
  `precompiledReferences`/raw-DLL-copy - the Redux SDK Manager's generated
  project already has `Unity.Entities` and friends available via installed
  UPM packages (`com.unity.entities`, `com.unity.collections`, etc.), and
  copying the raw DLLs in separately causes duplicate-assembly-identity
  conflicts at both compile and runtime.

## UI Toolkit / K2UI

K2D2's UI uses a library of custom UI Toolkit controls (`K2UI.*` -
`TabbedPage`, `ToggleButton`, `K2Slider`, etc.). Until the UxmlElement
migration below, these all used the legacy `UxmlFactory`/`UxmlTraits`
registration pattern. K2D2 itself extends `Redux.ExtraModTypes.KerbalMod`
and uses no BepInEx APIs at all - Redux loads it as a precompiled mod DLL
at runtime, not compiled into the Player build. Two real, non-obvious
problems specific to that loading model had to be worked out to get the
custom controls rendering at all (last confirmed on Redux build 26w33a):

- **Custom control types declared in a precompiled mod DLL never get their
  `UxmlFactory` auto-registered.** Unity's automatic factory scan
  (`VisualElementFactoryRegistry.RegisterUserFactories()`) only looks at
  assemblies Unity considers "known project assemblies"
  (`GetAllUserAssemblies()`), and a mod DLL loaded this way by Redux never
  appears in that list - regardless of whether the control uses the legacy
  `UxmlFactory` pattern or the modern `[UxmlElement]` attribute. Symptom:
  the UI renders everything else correctly, but drops literal placeholder
  text (`Unknown type: 'K2UI.ToggleButton'`, etc.) wherever a custom control
  should be. **Fix:** manually call the internal, protected
  `VisualElementFactoryRegistry.RegisterFactory()` via reflection for every
  custom control, once, at plugin init - see `KTools/K2UIFactoryRegistration.cs`,
  called from `K2D2_Plugin.cs`'s `OnInitialized()` before any UXML loads.
  **Note (superseded - see "UxmlElement migration" below):** the modern
  `[UxmlElement]`/`UxmlSerializedData` pattern was tried twice as an
  alternative fix around this time and confirmed broken both times, but
  specifically for the **AssetBundle** + precompiled-mod-DLL combination
  K2D2 was using back then (Unity's native managed-type resolution
  couldn't find the type at runtime even though it was correctly built
  into the DLL). By the time of the migration below, K2D2 had already
  moved off that AssetBundle entirely onto Redux's Addressables system
  (`AssetsLoader.LoadUxml()`, see the switch noted in `K2D2_Plugin.cs`) -
  the same loading shape Redux's own `Redux.SDK.Examples` UxmlExample mod
  uses and has confirmed working with `[UxmlElement]`. This old finding
  was about the AssetBundle path specifically, not `[UxmlElement]` in
  general, so it no longer applied once that switch had already happened.
- **This Unity version's base `VisualElement.UxmlTraits.Init()` has been
  gutted to a deprecation-warning stub - it no longer applies built-in
  attributes like `name`.** Every custom control that calls
  `base.Init(ve, bag, cc)` expecting it to set `ve.name` (the normal
  pre-this-Unity-version behavior) silently gets no `name` at all. K2D2's UI
  leans on `name` heavily for tab switching and element lookups
  (`panel.Q<T>(name)`), so this caused tab switching and per-tab element
  binding to fail silently. **Fix:** every custom control now declares its
  own `UxmlStringAttributeDescription` for `"name"` and applies it by hand
  right after the (now-inert) `base.Init()` call.
- **The shipped UI was loaded from a prebuilt `k2d2_ui.bundle`
  AssetBundle, built under an old pre-Unity-6 Editor (Unity 2022.3.5f1, per
  the original UI source project) and never rebuilt for this project's
  Unity 6000.5.8f1.** The version mismatch made UI Toolkit fail to clone
  the bundled `VisualTreeAsset` correctly (it silently produced a root
  element with zero children instead of erroring). **Fix:** located the
  original K2-D2 UI source project (raw `.uxml`/`.uss`, not just the
  compiled bundle), copied the full GUID-matched dependency closure
  (stylesheets, images, fonts - all wired by GUID via
  `project://database/...?guid=...`, so `.meta` files had to travel with
  every asset) into this project, and added an Editor-only tool
  (`Editor/RebuildK2D2UIBundle.cs`, menu item `K2D2 > Rebuild UI Bundle`) to
  rebuild `k2d2_ui.bundle` from source under the current Unity version. Run
  that tool again any time the UI source changes.

### UxmlElement migration

Unity 6.6 removes `UxmlFactory`/`UxmlTraits` entirely, so every `K2UI.*`
custom control (`K2Toggle`, `K2Slider`, `K2SliderInt`, `ToggleButton`,
`Group`, `ExFoldoutGroup`, `K2ProgressBar`, `InlineEnum`, `K2Avatar`,
`Console`, `StatusLine`, `K2AutoFitLabel`, `K2Compass`, `Graph.GraphLine`,
and the four `Tabs.*` classes) was converted to the modern
`[UxmlElement]`/`[UxmlAttribute]` source-generated pattern ahead of that
removal, following the pattern demonstrated in Redux's own
`Redux.SDK.Examples` repo (`Assets/UxmlExample/Code/Controls/`). See the
superseded note above for why the previous "confirmed broken" finding for
this pattern doesn't apply to K2D2's current Addressables-based UI loading.

`KTools/K2UIFactoryRegistration.cs` (the reflection-based manual
`VisualElementFactoryRegistry.RegisterFactory()` workaround from the first
bullet above) was deleted along with its call in `K2D2_Plugin.cs`'s
`OnInitialized()` - the newer attribute-based registration is handled by
Redux itself for mod assemblies, no manual step needed. The second bullet's
`name`-re-application workaround is also gone - UI Toolkit's own attribute
application sets `name` for every element type now, regardless of custom
control.

One non-mechanical wrinkle: the old `UxmlTraits.Init()` always applied
every declared attribute from the UXML bag, filling in its own
`defaultValue` for any attribute a tag didn't specify. The new attribute
system only calls a property's setter for attributes actually present in
the tag - so wherever a control's own C# default (its field initializer,
or nothing at all) didn't already match the old bag default, converting
it plainly would have silently changed behavior for any tag that omitted
that attribute. Found and fixed while converting:

- `K2Slider`/`K2SliderInt`: `_labelOnTop` was initialized to `true`, the
  opposite of the old `label-on-top` bag default of `false`; `Min`/`Max`
  relied on `Slider`/`SliderInt`'s own built-in `lowValue`/`highValue`
  rather than the old bag defaults (0/1 and 0/100 respectively); and the
  old `Init()`'s unconditional trailing `SliderValueChanged()`/
  `setLabels()` calls (needed for a fully consistent visual state even on
  a bare tag, e.g. `attitude.uxml`'s `elevation_slider`) had no equivalent
  without an explicit hook - added via an `AttachToPanelEvent` callback,
  the same "run my setup once actually attached" idiom `ExFoldoutGroup.cs`
  already used.
- `StatusLine`: every `<K2UI.StatusLine>` tag in the project omits
  `level="..."` entirely, relying on the old bag default of `Level.Normal`
  to apply its USS class - reproduced with an explicit `level =
  Level.Normal;` in the constructor.
- `ToggleButton`: `node.uxml`'s "pause" button omits `label="..."`
  entirely, relying on the old bag default of `"Toggle Button"` -
  reproduced the same way.
- `Group`, `K2ProgressBar`, `InlineEnum`: no current UXML tag actually
  omits the relevant attributes, but the same defensive default-setting
  was added in each constructor anyway (matching each old bag default
  exactly) in case a future tag does.

`TabsBar`, `TabButton`, and `TabPage` are never actually instantiated from
a UXML tag anywhere in the project (only ever via `new TabButton()` etc.
from `TabbedPage`'s own code, or built programmatically for the tab bar) -
their old `Init()` logic never actually ran in practice, so they converted
without needing any of the above.

`K2Compass` and `Graph.GraphLine` (both referenced by
`K2UIFactoryRegistration.cs`'s old factory list, but not present in the
first conversion pass) have since been converted too:

- `K2Compass`: `_angleRange`'s field initializer was `0`, not the old
  `angle-range` bag default of `90` - and both live usages
  (`attitude.uxml`'s and `Lift.uxml`'s `<K2UI.K2Compass name="heading" />`)
  are completely bare tags, so this would have divided by zero
  (`pixel_per_deg = width / AngleRange`) the moment either compass
  attached. Fixed the field default and set all three attributes
  explicitly in the constructor; also added a trailing `UpdateContent()`
  call to reproduce `Init()`'s old guaranteed call.
- `Graph.GraphLine`: found an unrelated pre-existing bug while auditing
  defaults - `_max_y`'s field initializer was `-1` (a copy-paste bug from
  `MinY`'s line above it), not `1`. Not currently reachable from any UXML
  tag, but fixed anyway since it would zero out the Y range
  (`MinY == MaxY`) the instant it was used bare. All seven attributes are
  now also set explicitly in the constructor, matching the old bag
  defaults.

`K2AutoFitLabel` converted trivially - it exposes no attributes beyond
`Label`'s own standard ones, which UI Toolkit already applies regardless
of custom control, so there was nothing else to change.

All `K2UI.*` custom controls are now converted; none use the removed
`UxmlFactory`/`UxmlTraits` pattern anymore.

## Node/orbit bugs fixed by subsystem

- **Lift** (`Pilots/Lift/Controlers/Ascent.cs`, `Final.cs`) - both had the
  `IKeplerPatch`-vs-`PatchedConicsOrbit` cast bug described above, which
  crashed the ascent autopilot every frame for the live vessel. Fixed by
  widening to the interface (only `Apoapsis`/`referenceBody.radius`/
  `TimeToAp` were actually needed, all available on `IOrbit`/`IKeplerPatch`).
- **Landing** (`Pilots/Landing/LandingPilot.cs`) - same cast bug in
  `computeValues()`, plus a second, much trickier case in
  `compute_real_collision()`. It originally depended on
  `GetStateVectorsFromUT()`, concrete-class-only like the cases above. The
  first fix attempt widened this to `IOrbit.GetTruePositionAtUT()`, which
  compiles and runs without error - but doesn't actually pair correctly with
  a body's local frame. This wasn't something reading the code would catch:
  real in-game testing showed the computed terrain altitude off by anywhere
  from hundreds of thousands to tens of millions of meters, on a calm orbit
  with no thrust involved, so collision never registered. (An earlier
  version of this note described `GetTruePositionAtUT()` as the fix - it
  wasn't, and this is the correction.) There's no direct documentation for
  this part of the Sim API, so the real fix came from reading two other KSP2
  mods' source for comparison: use `orbit.GetRelativePositionAtUTZup(ut)`
  instead, which is already relative to the orbit's reference body (no
  reframing needed) but comes back in "Zup" convention - its Y and Z
  components need swapping before use - paired with
  `body.SimulationObject.transform.celestialFrame`, not
  `body.coordinateSystem` (confirmed against KontrolSystem2's
  `BodyWrapper.cs`, which builds every body-relative position the same way).
  Confirmed working via full autopilot landings on both the Mun and a Kerbin
  boostback.
- **Lift** (`Pilots/Lift/LiftPilot.cs`) - a second bug, unrelated to the
  Redux port itself: `EndLiftPilot()` ended a run by setting the internal
  `status` field straight to `Off`, which is what `isRunning`'s getter reads
  from but skips its setter entirely - so `is_running_event` never fired
  when the ascent finished on its own. Anything bound to that event (the
  Start/Stop toggle's pressed state, K2's avatar animation) stayed showing
  "running" even after the pilot had already stopped. Only affected the
  natural end-of-run path; stopping it manually via the toggle worked fine
  since that already goes through `isRunning`'s setter. Fixed by having
  `EndLiftPilot()` set `isRunning = false` instead, matching how Node/Landing
  already end their own runs.
- **Landing UI** (`LandingUI.cs`) - once collision detection actually
  started working, an early `return` in `onUpdateUI()` (guarded on
  `!pilot.collision_detected`) was found to also skip the Touch Down
  button's visibility and the Brake/Touch Down/Waiting status text whenever
  collision wasn't currently predicted mid-descent - freezing both at
  whatever they last showed. Fixed by removing the early return.
- **Docks** (`Pilots/Docks/SelectTargetUI.cs`) - `buildControlList()`/
  `buildTargetList()` built their dropdown lists with `list.Append(...)`,
  which (with `System.Linq` in scope) silently resolved to the
  non-mutating LINQ `Enumerable.Append()` instead of `List<T>.Add()`,
  leaving both dropdowns permanently empty. Also, `buildTargetList()`
  assigned its result to the wrong dropdown (`control_from_drop` instead of
  `target_drop`), likely a copy-paste from the method above it. Both fixed.
- **Drone** (`DronePilot.cs`) - `wanted_altitude` and `wanted_speed` were
  bound to the same persisted settings key, so saving one silently
  overwrote the other. Fixed to use separate keys.
- **Window not opening** (`UI/K2D2Window.cs`) - `OnEnable` used
  `GetComponent<UIDocument>()` to find the window's root element, a
  SpaceWarp1-era pattern. The current `UitkForKsp2.API.Window.Create(...)`
  returns a `PanelRenderer` instead, which has no `UIDocument` to find.
  Fixed by getting the root element via
  `PanelRenderer.RegisterUIReloadCallback` instead.

Everything else in `Source/Pilots/` (base controllers, Attitude, and a full
pass over Docks/Landing/Lift/Nodes/Staging/Drone beyond the bugs above) has
been verified call-by-call against the real Redux assemblies with no further
issues found. `K2UI/` itself has not been given the same call-by-call
verification pass (only the two structural issues above were investigated).

## Landing/Node fixes before 1.3.0

Four issues cleared before cutting 1.3.0, all in-game-tested behavior fixes
rather than Redux porting bugs:

- **Precision Landing gated to vacuum bodies only.** Nothing in Circularize/
  DeorbitBurn currently supports an atmospheric descent, so turning Precision
  Landing on at an atmospheric body previously just silently did nothing
  useful. `LandingUI.cs`'s Precision Landing toggle now only exists on the
  new Vacuum settings panel (see the profile split below) - an atmospheric
  body shows a short explanatory note instead. `LandingPilot.cs`'s
  `isRunning` setter also carries a defense-in-depth check
  (`settings.precision_landing.V && !LandingProfile.IsAtmospheric`) so
  Circularize/DeorbitBurn can never be entered on an atmospheric body even
  from a manually-edited settings file.
- **Deorbit-burn search biased toward nearby candidates.** `LandingTargeting.
  FindBestDeorbitBurn`/`TryEvaluateCandidate` picked whichever sampled burn
  time scored the lowest predicted ground-miss distance, with no cost for
  how far around the orbit that candidate sat - so a burn point that was
  numerically a hair better but almost a full orbit away could win outright,
  even though a nearer candidate would have left the plane-trim budget
  (`normalDeltaV`) enough room to actually matter. Fixed by adding a
  `distance_penalty_per_second` term (0.2 m per second of wait, a first-pass
  heuristic - worth tuning once seen flying) to the score used only for
  picking a winner; the true predicted miss distance (`bestErrorM`) is
  untouched, so logging and the in-game Target Error readout still show the
  real number.
- **Node's SAS now stays locked to the maneuver vector for the whole burn.**
  `BurnManeuver.Update()` (`Pilots/Nodes/Controlers/BurnManeuvre.cs`) was
  dropping from `AutopilotMode.Maneuver` to plain `AutopilotMode.
  StabilityAssist` the instant a burn started, unless the "Rotate During
  Burn" toggle (Node tab, Experimental section) was turned on - which
  defaulted to off. Flipped that default to `true`; the toggle itself is
  unchanged; for anyone who wants the old fixed-orientation behavior back.
  Since `BurnManeuver` is shared by every burn in the mod, this also changes
  Lift's final circularize and Landing's Circularize/DeorbitBurn/
  MidCourseCorrection burns, not just Node's - intentional, since more
  accurate burns are wanted everywhere.
- **Atmospheric and Vacuum landings now use two fully separate settings
  profiles**, so tuning one can no longer touch or wreck the other. Landing's
  settings used to live in the same shared `k2d2_settings.json` every other
  tab's settings do; they now live in two new dedicated files,
  `k2d2_landing_atmo.json`/`k2d2_landing_vac.json`, switched automatically
  based on whether the current body has an atmosphere
  (`Pilots/Landing/LandingProfile.cs`, kept current every tick from
  `LandingPilot.Update()`). `SettingsFile`/`Setting<T>`/`ClampSetting<T>`
  (`KTools/`) were generalized to support named file instances beyond the
  original singleton, purely additively - every other tab's settings are
  unaffected. `LandingSettings`/`TouchDown` each now hold two full,
  independent instances (`settings_atmo`/`settings_vac`,
  `TouchDown`'s own internal `atmo`/`vac`), selected via a pass-through
  property everywhere else in the codebase already reads through
  (`LandingPilot.settings`, `TouchDown`'s public tunable properties) - no
  other call site needed to change. The Landing tab's UI (`Landing.uxml`)
  now has two full parallel panels (Atmo panel: Warp/Brake/Touch Down only;
  Vacuum panel: those plus Precision Landing/RCS fine correction), each
  bound exactly once at tab-init and never rebound - `K2Page.onInit()` only
  ever runs once per session, so a single settings object dynamically
  swapped underneath an already-bound `K2Slider`/`K2Toggle` would leave the
  UI stuck on whichever profile was active the first time the tab opened.
  Landing's Reset button now resets both files together instead of just the
  shared one.
  One consequence worth flagging: this moves Landing's settings out of the
  main settings file entirely, so anything already tuned there (burn
  timing, touchdown altitude, etc.) starts over at coded defaults in the new
  files rather than carrying over automatically.

Precision landing's accuracy rests entirely on TouchDown's own closed-loop
steering (cross-track/along-track error correction, RCS fine correction,
proportional arc extend/shorten - see `TouchDown.cs`), started early enough
by `compute_startBurn`'s lateral-correction-time and altitude-margin floors.
