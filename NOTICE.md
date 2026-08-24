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
`TabbedPage`, `ToggleButton`, `K2Slider`, etc.), all using the legacy
`UxmlFactory`/`UxmlTraits` registration pattern. Two real, non-obvious Redux/
BepInEx-specific problems had to be worked out to get them rendering at all:

- **Custom control types loaded from a BepInEx-injected mod assembly never
  get their `UxmlFactory` auto-registered.** Unity's automatic factory scan
  (`VisualElementFactoryRegistry.RegisterUserFactories()`) only looks at
  assemblies Unity considers "known project assemblies"
  (`GetAllUserAssemblies()`), and a mod DLL injected by BepInEx never
  appears in that list - regardless of whether the control uses the legacy
  `UxmlFactory` pattern or the modern `[UxmlElement]` attribute. Symptom:
  the UI renders everything else correctly, but drops literal placeholder
  text (`Unknown type: 'K2UI.ToggleButton'`, etc.) wherever a custom control
  should be. **Fix:** manually call the internal, protected
  `VisualElementFactoryRegistry.RegisterFactory()` via reflection for every
  custom control, once, at plugin init - see `KTools/K2UIFactoryRegistration.cs`,
  called from `K2D2_Plugin.cs`'s `OnInitialized()` before any UXML loads.
  **Note:** the modern `[UxmlElement]`/`UxmlSerializedData` pattern was
  tried twice as an alternative fix and confirmed broken both times for
  this AssetBundle + BepInEx combination (Unity's native managed-type
  resolution can't find the type at runtime even though it's correctly
  built into the DLL) - don't spend time on that path again for a
  precompiled-plugin + AssetBundle UI.
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

## Node/orbit bugs fixed by subsystem

- **Lift** (`Pilots/Lift/Controlers/Ascent.cs`, `Final.cs`) - both had the
  `IKeplerPatch`-vs-`PatchedConicsOrbit` cast bug described above, which
  crashed the ascent autopilot every frame for the live vessel. Fixed by
  widening to the interface (only `Apoapsis`/`referenceBody.radius`/
  `TimeToAp` were actually needed, all available on `IOrbit`/`IKeplerPatch`).
- **Landing** (`Pilots/Landing/LandingPilot.cs`) - same cast bug in
  `computeValues()`, plus a second, trickier case in
  `compute_real_collision()`: it depended on `GetStateVectorsFromUT()`,
  which only exists on the concrete `PatchedConicsOrbit` class. Fixed by
  switching to `IOrbit.GetTruePositionAtUT()`, an interface member that
  returns the same fully-resolved, coordinate-system-correct result -
  confirmed working, real terrain-collision detection now functions
  correctly for the live vessel with no concrete-class dependency.
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
