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
base. That port was never built or tested against a live game (single day of
commits, Unity project still pinned to an old editor version, README never
updated past the original SpaceWarp+BepInEx install instructions), and its
own commit message flags that Flight Plan integration
(`K2D2OtherModsInterface.cs`) still needs to be rebuilt - it's currently a
fully commented-out stub, with `Pilots/Nodes/FlightPlanCall.cs`'s
`Circularize()` gracefully returning `false` until that's done. This project
starts from that port's source (`Assets/K2D2/Code`) rather than porting fresh
from the original, to build on the real work already there instead of
duplicating it.

## This project's own verification work

Following the same practice established in the sibling project
KerbalAutopilot: every API call this project touches gets verified against
the real, current KSP2 Redux assemblies (via IL inspection, primarily
`monodis`) before being trusted or changed - not assumed correct because it
compiles-by-eye or "looks right." Fixes made this way get their own doc
comment at the point of the fix, plus an entry here once a session's
worth of verification work is done. As of this note:

- `Source/K2D2_Plugin.cs` - `ValidScene()` called
  `GlobalGameState.GetState()`, which does not exist on the current
  `KSP.Game.GameStateMachine` (confirmed via full member enumeration against
  the real `Assembly-CSharp.dll`). This gated the mod's entire per-frame
  pilot update pipeline, so it would have thrown a `MissingMethodException`
  immediately on load. Fixed to `GlobalGameState.GetGameState().GameState`,
  the real accessor.
- `Source/K2D2_Plugin.cs` and `Source/KTools/AssetsLoader.cs` -
  `SpaceWarpPluginDescriptor.Folder` (`SWMetadata.Folder`) is a
  `System.IO.DirectoryInfo`, not a `string`, in the current assemblies. The
  original code string-concatenated it directly, which happens to work via
  `DirectoryInfo`'s implicit `ToString()`, but was changed to the correct,
  intended `.Folder.FullName` accessor.

- `Source/Pilots/BaseControllers/*.cs`, `Source/Pilots/Attitude/*.cs` -
  verified in full (`Pilot`, `BaseController`, `ComplexControler`,
  `ControllerManager`, `ExecuteController`, `AttitudePilot`, `AttitudeUI`).
  Every external call (KSP.Game/KSP.Sim/UnityEngine.UIElements) matched the
  current assemblies exactly - no fixes needed. This is the foundation every
  other pilot builds on, so it being clean is a meaningfully good sign for
  what's ahead, though it doesn't guarantee any specific leaf pilot is fine.
- `Source/KSPService/*.cs` and `Source/KSPService/ManeuverCreator/*.cs` -
  verified; five real bugs found and fixed:
  - `ManeuverCreator.cs` (`CreateManeuverNode_Co`) and `KSPVessel.cs`
    (`getCurrentOrbitSpeed`) both used `VesselComponent.Orbit` as if it were
    `PatchedConicsOrbit` directly. It's actually typed `KSP.Sim.IKeplerPatch`
    in the current assemblies (an interface, confirmed via full member
    enumeration - no `orbitalSpeed`/concrete members on the interface
    itself) - would not compile without an explicit cast. Fixed both to
    cast to `PatchedConicsOrbit` first, matching the pattern the rest of
    `ManeuverCreator.cs` already used correctly via `GetLastOrbit()`.
  - Four call sites (`KSPVesselInformation.GetManeuverNodeVector`,
    `KSPVessel.GetAltimeterDisplayMode`, `GetDisplaySpeed`,
    `GetSpeedDisplayMode`) called `.GetValue()` on a
    `KSP.Api.CoreTypes.PropertyExternal<T>` wrapper - a pre-port
    SpaceWarp1-era leftover method name that no longer exists anywhere in
    the current assemblies (confirmed absent from the entire string table
    of Assembly-CSharp.dll/ReduxLib.dll/both SpaceWarp2 DLLs). Fixed to the
    real accessor, the `.Value` property.
  - `KSPVessel.GetNextManeuveurNode()` called `.Count()` (a LINQ extension
    method) on a `List<ManeuverNodeData>` with no `using System.Linq;` in
    the file - fixed to the `.Count` property, matching correct usage
    already present elsewhere in the same codebase.
  - `ManeuverManager.cs` used `FunctionQueue`/`FunctionObject`
    (namespace `K2D2`, in `CustomQueue.cs`) with no `using K2D2;` at all -
    a plain missing-using compile error, unrelated to the Redux API itself,
    likely a leftover from restructuring folders during the port. Fixed.

  Flagged but NOT fixed (need follow-up, not confirmed broken):
  `getOrbitalVelocity()`/`GetAngularSpeed()` in `KSPVessel.cs` return raw,
  coordinate-system-tagged vectors with no reframing - exactly the category
  of bug KerbalAutopilot's own history repeatedly found (a value that's
  technically correct but expressed in the wrong, vessel-rotating frame).
  Whether this actually matters depends on how the leaf pilots (Landing,
  Lift, Docks, Attitude) consume these - worth checking when we get there.
  Also unverified: `ManeuverCreator.cs`'s `Map3DView`/`Map3DManeuvers`
  gizmo-update calls (`GetNodeDataForVessels`/`UpdatePositionForGizmo`) -
  `monodis` crashed before reaching that type's method table, so these are
  corroborated only by field-shape evidence, not a confirmed signature
  match. `KSPVesselInformation.cs` also turned out to be unreferenced dead
  code (and in a mismatched namespace, `KSP2FlightAssistant.KSPService`
  instead of `K2D2.*`) - not blocking anything today, fixed for correctness
  anyway since it was cheap, but worth knowing it isn't actually wired up.

K2UI/ (the custom UI Toolkit control library) has not yet been verified
against the current assemblies - see the note on `UxmlTraits` deprecation
warnings further down. Treat it as UNVERIFIED until it gets its own entry.

### `UI/K2D2Window.cs` - window never opened (first real in-game test)

The mod booted and the app bar icon showed, but clicking it did nothing.
`OnEnable` called `GetComponent<UIDocument>()` to get the window's root
VisualElement - a SpaceWarp1-era pattern. The current
`UitkForKsp2.API.Window.Create(...)` call in `K2D2_Plugin.cs` returns a
`PanelRenderer` instead, which has no `UIDocument` component to find and no
direct `.rootVisualElement` - so the lookup silently returned null and the
very next line NREd before any control (tabs, close button, title bar,
drag manipulator) got wired up. Exactly the same root cause
KerbalAutopilot's `MainAppWindow.cs` already hit and documented for this
same Redux API. Fixed by getting the root element via
`PanelRenderer.RegisterUIReloadCallback` instead (fires once immediately,
and again on any later live UI reload), with a `_bound` guard so the
one-time control-wiring code can't run twice.

### Correction: the `.GetValue()` -> `.Value` fix above was wrong

A real Unity build (the first actual compile against the live Redux
assemblies, not our incomplete local snapshot) surfaced `CS1061:
'PropertyExternal<Vector3>' does not contain a definition for 'Value'` -
directly contradicting the `.GetValue()` -> `.Value` fix recorded above. The
original monodis-based check that produced that fix was flawed (it appears
to have checked for the string `GetValue` across the assemblies' string
tables and concluded it was absent, but never actually enumerated
`PropertyExternal<T>`'s own method table to confirm what *was* there).

Re-verified properly this time via full MethodDef-table enumeration using
`dnfile` (a pure-Python ECMA-335 metadata reader) against the real
`Assembly-CSharp.dll`, sidestepping `monodis --method`'s crash on this large
an assembly entirely. Result: `KSP.Api.CoreTypes.PropertyExternal<T>` has a
public `GetValue()` method and no `Value` member of any kind (no property,
no field - confirmed by walking its full field table too, which is all
private backing fields for the `GetValue`/`SetValue` delegates). The
original pre-port `.GetValue()` calls were correct all along. Reverted at
all 4 call sites: `KSPVesselInformation.GetManeuverNodeVector`,
`KSPVessel.GetAltimeterDisplayMode`/`GetDisplaySpeed`/`GetSpeedDisplayMode`.

### Landing / Lift / Nodes - first real bugs, found via live build errors

Before dedicated verification reached these subsystems, a live build against
the actual assemblies surfaced the same `VesselComponent.Orbit` interface-vs-
concrete-type issue documented above (IKeplerPatch vs. PatchedConicsOrbit),
this time at 5 more call sites the earlier KSPService-only pass never
touched: `Pilots/Nodes/FlightPlanCall.cs` (`getCurrentOrbit`), `Pilots/Lift/
Controlers/Ascent.cs` (`computeValues`), `Pilots/Lift/Controlers/Final.cs`
(`getOrbit`), and `Pilots/Landing/LandingPilot.cs` (two call sites -
`computeValues` and `compute_real_collision`). All fixed with the same
explicit cast to `PatchedConicsOrbit`. Also found 2 more `ReferenceBodyConstants`
call sites in `ManeuverCreator.cs` (`GetOrbitalVelocity`/
`GetOrbitalPerifocalVelocityVector`) needing the identical cast, beyond the
one already fixed in `CreateManeuverNode_Co`.

### `K2D2.asmdef` - missing Unity.Entities reference

`KSP.Sim.impl.UniverseModel` (used by `KSPService/TimeWarpTools.cs`'s
`SetIsPaused` and `Pilots/Docks/SelectTargetUI.cs`'s `buildTargetList`)
extends `Unity.Entities.SystemBase` directly - confirmed via TypeDef/TypeRef
metadata inspection of `Assembly-CSharp.dll`. Referencing any member on
`UniverseModel`, even ones it declares itself, requires the compiler to
resolve that base class, which lives in `Unity.Entities.dll` - a DLL absent
from the Redux SDK Manager's generated `K2D2.asmdef` (its `precompiledReferences`
list, though large, doesn't include any of Unity's DOTS/ECS package
assemblies). Added `Unity.Entities.dll` plus its likely dependency chain
(`Unity.Entities.Hybrid.dll`, `Unity.Collections.dll`, `Unity.Burst.dll`,
`Unity.Mathematics.dll`, `Unity.Transforms.dll`, `Unity.Scenes.dll`,
`Unity.Serialization.dll`, `Unity.ResourceManager.dll`,
`Unity.Addressables.dll`) - all confirmed physically present in the real
Redux install's `Managed/` folder before adding, so this isn't a guess at
DLL names that may not exist.

**Follow-up**: naming those 10 DLLs in `precompiledReferences` alone did not
fix the build - the errors persisted identically. The actual mechanism
turned out to be: the Redux SDK Manager's generated project keeps its own
local copy of ~90 of the game's managed DLLs as a Unity embedded package at
`Packages/KSP2_x64/` (confirmed via `package.json` there), and
`precompiledReferences` only resolves against DLLs Unity has actually
imported as Plugin assets somewhere in the project - it doesn't pull
arbitrary DLLs from the asmdef text alone. `Unity.Entities.dll` and the 9
others simply weren't among the ~90 DLLs that package was seeded with.
Fixed by copying all 10 files directly into `Packages/KSP2_x64/` alongside
the existing ones (no `.meta` files provided - Unity should auto-generate
them on next import/domain reload, the same way it did for the newly-added
`.cs` files).

**Second follow-up - that fix caused a real regression.** Copying the 10
raw DLLs in without matching `.meta` files let Unity import them with its
*default* PluginImporter settings, which auto-reference a plugin DLL into
every assembly in the project unless told otherwise. This collided with
packages this project already has properly installed via Package Manager
(`Library/PackageCache` confirms `com.unity.entities`, `com.unity.collections`,
`com.unity.burst`, `com.unity.mathematics`, `com.unity.serialization`,
`com.unity.addressables` are all already present) - the raw DLL copies and
the UPM packages' own compiled assemblies both ended up in scope
simultaneously, producing `CS0121` "ambiguous call" errors inside
`com.unity.collections`'s own source (`NativeList.cs` etc., under
`Library/PackageCache`), since its own extension methods now had two
identical-signature candidates to resolve between.

None of those 10 DLLs should have been added as raw precompiled references
in the first place - every one of them is already provided by an installed
UPM package. Corrected `K2D2.asmdef` to reference `Unity.Entities` (the
actual base-class assembly `SystemBase` lives in) as a proper assembly-
definition reference in `references`, not a `precompiledReferences` entry,
and removed all 10 DLL names from `precompiledReferences` (back to the
original SDK-generated list). Since the raw DLL files themselves can't be
deleted through this session's tooling, each was given a correct `.meta`
(`isExplicitlyReferenced: 1`, matching every other DLL in `Packages/KSP2_x64/`)
so Unity stops auto-referencing them into every assembly - they become
inert, unused clutter rather than active conflicts. Ideally they'd be
deleted by hand at some point, but they're harmless as long as their
`.meta` stays in this state.

**Third follow-up - "harmless" was wrong; these DLLs are the likely cause
of the window-doesn't-open symptom.** After the `K2D2Window.cs`
UIDocument→PanelRenderer fix (below) was pushed, the mod boots and the
app-bar icon appears, but clicking it still doesn't open the window.
Unity's own Editor console flags exactly why, unprompted, on every one of
the 10 DLLs added above: `Plugin 'Packages/ksp2_x64/Unity.Entities.dll'
has the same filename as Assembly Definition File
'Packages/com.unity.entities/Unity.Entities/Unity.Entities.asmdef'.
Rename the assemblies to avoid hard to diagnose issues and crashes.` The
`isExplicitlyReferenced: 1` `.meta` fix above stopped the *compile-time*
ambiguity (`CS0121`), but it does nothing about two physically different
assemblies sharing the same assembly identity (`Unity.Entities`,
`Unity.Collections`, etc.) both being loaded into the same AppDomain at
*runtime* - which is precisely what Unity's warning is calling "hard to
diagnose issues and crashes." K2D2 does touch `KSP.Sim.impl.UniverseModel`
(via `Docks/SelectTargetUI.cs` and `KSPService/TimeWarpTools.cs`), which
derives from `Unity.Entities.SystemBase` - a very plausible path for a
type-identity mismatch to throw or silently fail somewhere between the
app-bar icon registering successfully and the window ever showing.
Recommended fix: delete all 10 raw DLLs (and their `.meta` files) from
`Packages/KSP2_x64/` - this session's device-bridge tooling still can't
delete files on Reese's machine, so this needs to be done by hand, ideally
via Unity's own Project window so the `.meta` files get cleaned up too.
The already-installed UPM packages (`com.unity.entities`,
`com.unity.collections`, `com.unity.burst`, `com.unity.mathematics`,
`com.unity.serialization`, `com.unity.addressables`, confirmed present in
`Library/PackageCache`) supply everything `K2D2.asmdef`'s `references`
entries need once the raw duplicates are gone.

**Fourth follow-up - the DLL collision was a red herring for this
particular symptom; the real cause was a Unity Editor Burst/Entities
domain-reload race, unrelated to K2D2's code.** Reese deleted 9 of the 10
duplicate DLLs (`Unity.Mathematics.dll` + its `.meta` are still present in
`Packages/KSP2_x64/` as of this note - worth clearing out too, though it
turned out not to be the actual blocker here). The window still didn't
open afterward, and the Editor console error Reese pasted told the real
story: an `InvalidOperationException` - *"Burst compilation cannot be
scheduled after domain unload or before Burst has been initialized
following a domain reload"* - thrown from deep inside
`Unity.Entities.TypeManager.Initialize()`, called from `com.unity.entities`'s
own Editor-side static constructors (`BindingRegistry..cctor()`,
`AttachToEntityClonerInjection..cctor()`,
`Unity.Scenes.Editor.TypeDependencyCache..cctor()`) via
`UnityEditor.EditorAssemblies:ProcessInitializeOnLoadAttributes` during a
domain reload. None of these stack traces contain any K2D2, ReduxLib, or
Assembly-CSharp code - this is Unity's own Entities/Burst packages racing
each other on script recompile, not a K2D2 bug. Root cause, best
understanding: before this session, nothing in K2D2 referenced
`Unity.Entities`, so its Editor tooling likely sat dormant in this project;
once `K2D2.asmdef` started referencing it (for the `SystemBase` build
errors), Entities' Editor-side type registration started actively running
on every domain reload and hit a known Burst-not-ready-yet race. Because a
failed .NET static constructor poisons that type for the rest of the
process, this can leave `Unity.Entities.TypeManager` permanently broken
for the rest of that Editor session - which would explain code touching
`UniverseModel` (`SelectTargetUI.cs`, `TimeWarpTools.cs`) misbehaving in
Play mode afterward even though nothing about K2D2 itself changed.
Recommended fix (not yet confirmed working): fully close and restart the
Unity Editor (not just recompile, not just exit/re-enter Play mode) and
let the initial import/compile fully settle before pressing Play, so
Burst initializes cleanly before Entities' Editor tooling tries to use it.

**Fifth follow-up - found the actual cause, via diagnostic logging + the real
game's `Player.log` (not the Editor console).** Added explicit logging to
every step of `K2D2Window`'s `OnEnable`/`OnUiReload` wiring (see the "window
never opened" section below) and read the resulting log directly from
`%LOCALLOW%\Intercept Games\Kerbal Space Program 2\Player.log` on Reese's
machine via the device bridge, rather than relying on manually copy-pasted
Editor console excerpts (which had only ever captured Editor-time domain
reload noise, never an actual "Test in Game" run - Redux SDK's "Test in
Game" launches the real standalone KSP2 process, so anything logged there
goes to the *Player* log, not the Unity Editor's own log). The real
exception, previously invisible because Unity swallows it (this is thrown
from inside `PanelRenderer.InvokeUIReloadCallbacks()`'s delegate-invoke
wrapper, caught the same way OnEnable exceptions are caught - logged, not
propagated):

```
ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection.
  at System.Collections.Generic.List`1[T].get_Item (System.Int32 index)
  at UnityEngine.UIElements.VisualElement+Hierarchy.get_Item (System.Int32 key)
  at UnityEngine.UIElements.VisualElement.get_Item (System.Int32 key)
  at K2D2.UI.K2D2Window.OnUiReload (...)
```

This is `_rootElement = root[0];` - the very first line of `OnUiReload` -
throwing because `root` (the `TemplateContainer` UI Toolkit creates after
cloning `K2D2_Window.uxml`'s `VisualTreeAsset`) has **zero children**.
Since this throws before `_rootElement` is ever assigned, every other
line of `OnUiReload` never runs, and `IsWindowOpen`'s `_rootElement?.Show(value)`
is permanently a no-op - exactly matching "the icon appears but clicking
it does nothing."

Root cause, best current understanding: K2D2's entire UI is loaded from a
single prebuilt `Copied/assets/bundles/k2d2_ui.bundle` (~46 MB,
`AssetBundle.LoadFromFile()` + `Bundle.LoadAsset<VisualTreeAsset>(...)`) -
confirmed via a full recursive listing of `Assets/K2D2/` that there is no
raw `.uxml`/`.uss` source anywhere in this Unity project, only that
prebuilt bundle. AssetBundles are tied fairly tightly to the Unity version
(and UI Toolkit serialization format) they were built with. This bundle
was almost certainly built under an old, pre-Unity-6 SpaceWarp1-era Unity
version, and its serialized `VisualTreeAsset` - especially the custom
`UxmlFactory`/`UxmlTraits`-based K2UI controls it references
(`TabbedPage`, `K2Slider`, etc.) - likely fails to correctly resolve
against this project's freshly-compiled Unity 6 K2UI assembly when UI
Toolkit tries to clone it, producing a template container with no
children instead of an error. This ties together several previously-loose
threads from this session: the CS0618 UxmlTraits deprecation warnings
Reese screenshotted earlier, and the "flagged this as a future concern"
note about the prebuilt bundle's version-mismatch risk when the
Addressables question was first answered - this looks like that risk
having actually materialized.

**Not yet fixed.** The real fix is almost certainly rebuilding
`k2d2_ui.bundle` from actual `.uxml`/`.uss` source under this project's
current Unity 6000.5.8f1 install, rather than continuing to load the old
prebuilt one - but that requires the original UI source files, which
aren't present anywhere in this Unity project as it stands. Next step is
finding out whether Reese has access to the original K2-D2 mod's Unity UI
source project (not just its compiled/distributed mod folder) so the
bundle can be rebuilt.

**Sixth follow-up - the original UI source project has been located, and
the version-mismatch theory is now strongly corroborated by direct
evidence (not yet fixed).** Reese connected the original `k2d2-master`
GitHub repo (downloaded to `Downloads\k2d2-master`). It contains a full,
separate Unity project at `src/K2D2.Unity/K2D2.Unity/` -
`ProjectSettings/ProjectVersion.txt` confirms it was authored under
**Unity 2022.3.5f1**, a full major version behind this project's
6000.5.8f1, which is exactly the kind of gap that would explain a
UxmlTraits-serialized `VisualTreeAsset` failing to clone correctly. Its
`Assets/UI/K2D2_UI/` folder contains the real, editable UXML/USS source
for every page of K2D2's window - `K2D2_Window.uxml`, `Dock.uxml`,
`Landing.uxml`, `Lift.uxml`, `node.uxml`, `attitude.uxml`, `about.uxml`,
`StatusGroup.uxml`, `K2D2.uss` - and the internal bundle path
`AssetsLoader.LoadUxml()` already asks for (`Assets/UI/K2D2_UI/{path}`)
matches this folder's layout exactly, confirming the shipped
`k2d2_ui.bundle` was built directly from it.

Every one of those 8 UXML files (and `K2D2.uss`) was read in full. All
cross-references between them - templates (`<ui:Template src="...">`,
`<ui:Instance template="...">`), stylesheets (`<Style src="...">`), and
images/fonts embedded via `background-image: url(...)` /
`-unity-font-definition: url(...)` - are wired by **GUID**
(`project://database/Assets/...?guid=...`), not by path alone. The
dependency closure is: `Assets/UI/K2D2_UI/*.uxml` + `K2D2.uss`;
`Assets/Runtime/K2UI/USS/*.uss` (`K2UI.uss`, `ToggleButton.uss`,
`K2Toggle.uss`, `K2Slider.uss`, plus `K2Tabs.uss`/`compas.uss`/
`Graph.uss`/`Ids.uss` referenced transitively); `Assets/Runtime/K2UI/Images/`
(`Cross.png`, `gear.png`); `Assets/Images/` (`cropped_40px.png`,
`Cross.png`, `Staging.png`, `pause.png`, `k2d2_big_icon.png`, plus a few
not directly referenced by the pages above but cheap to bring along:
`gear.png`, `icon.png`, `Light_Button.png`, `shadow.png`); and
`Assets/Fonts/Caravan.asset` + `Caravan.ttf` (the one font actually
referenced, out of the many in that folder). None of these top-level
folder names (`UI`, `Runtime`, `Images`, `Fonts`) currently exist under
this project's `Assets/` root, so copying them in is purely additive - no
existing files would be overwritten. Because the cross-references are
GUID-based, the `.meta` file has to travel with every asset copied over
(that's what carries the GUID) - copying just the visible files and
letting Unity mint fresh GUIDs would silently break every template/style/
image reference again, in a way that would likely reproduce this exact
"zero children" symptom.

Also checked whether this project's own ThunderKit build pipelines
(`Assets/K2D2/Pipelines/Build for Editor.asset`, `Build for Player.asset`)
already rebuild the asset bundle as part of building the mod - they
don't. Both only run `StageFolder` / `StageGeneratedTextAssets` /
`StageAddressablesGroups` / `StageAssemblies` jobs; there is no
`BuildAssetBundles` step anywhere in either pipeline. This confirms
`Copied/assets/bundles/k2d2_ui.bundle` (48,174,928 bytes, byte-identical
to the copy sitting in `k2d2-master/plugin_template/assets/bundles/`) is a
static, manually-built artifact that was carried over unchanged from the
original SpaceWarp1-era project and has never been rebuilt since - nothing
in this project's normal build flow would have caught the version
mismatch, or will regenerate the bundle automatically once the source is
in place.

**Still not yet fixed** - two real options were identified for actually
closing this out, and which one to take needs a decision before touching
the live Unity project further:

1. Copy the dependency closure above into this project's `Assets/`
   (preserving `.meta` files), then rebuild `k2d2_ui.bundle` under Unity
   6000.5.8f1 (e.g. via `Window > Asset Management > AssetBundles` after
   assigning `K2D2_Window.uxml` + friends an AssetBundle name, or a small
   custom Editor script calling `BuildPipeline.BuildAssetBundles`) and
   have `AssetsLoader`/`K2D2_Plugin.cs` keep loading it exactly as they do
   today. Lowest code risk (zero C# changes), but needs a manual
   AssetBundle build step this project's pipelines don't currently do for
   K2D2, run correctly by hand.
2. Copy the same assets in and switch `AssetsLoader.LoadUxml`/
   `K2D2_Plugin.cs` to reference the `VisualTreeAsset` as a normal project
   asset (e.g. via a `Resources/` folder + `Resources.Load<VisualTreeAsset>`)
   instead of an `AssetBundle.LoadFromFile()` at all, removing the version-
   mismatch risk category entirely going forward. Not yet verified whether
   Redux/ThunderKit's mod build actually bakes a `Resources/` folder's
   contents into a shipped mod DLL the way a full Unity Player build would
   - needs confirming (ideally against a working Redux mod that does this)
   before committing to it.

**Seventh follow-up - decision made: option 1, rebuild the bundle now
(not yet verified end-to-end - needs a real Unity test).** Reese asked for
whichever option means less work later, given K2UI is going to be
modernized off `UxmlTraits` at some point anyway. A third option -
Addressables, via `StageAddressablesGroups`, which is already an active
(if currently unused-by-K2D2) step in both `Build for Editor.asset` and
`Build for Player.asset`, and `Assets/AddressableAssetsData/` already
exists in this project - was also considered, since it would remove the
manual-rebuild-step class of problem entirely and looks like the mechanism
this Redux/ThunderKit template was actually built around. It was set
aside for now rather than adopted blind: `Window.Create()` takes a
`VisualTreeAsset` synchronously (confirmed via IL earlier this session),
Addressables loading is normally async, and there's no existing precedent
anywhere in this project of a UI Toolkit tree being shipped through
Addressables to check the integration actually works cleanly against.
Rather than gamble on an unverified architecture change while the window
is still broken, Addressables migration is being deliberately deferred to
sit alongside the already-planned "modernize K2UI off deprecated
UxmlTraits" work, where it can be tried and tested properly rather than
guessed at under time pressure.

For now: the full dependency closure identified above (`Assets/UI/K2D2_UI/`
- all 8 UXML pages + `K2D2.uss`, `Assets/Runtime/K2UI/USS/` - all 8
stylesheets, `Assets/Runtime/K2UI/Images/` - `Cross.png` + `gear.png`,
`Assets/Images/` - all 9 images, `Assets/Fonts/` - `Caravan.asset` +
`Caravan.ttf`) has been copied into this project with matching `.meta`
files, preserving every GUID the UXML/USS cross-references rely on. A new
Editor-only tool, `Editor/RebuildK2D2UIBundle.cs` (+ its own
`K2D2.Editor.asmdef`, `includePlatforms: ["Editor"]` so it never ships
with the mod), was added: `K2D2 > Rebuild UI Bundle` marks every
`VisualTreeAsset`/`StyleSheet` under `Assets/UI/K2D2_UI` with the
`k2d2_ui.bundle` AssetBundle name and calls
`BuildPipeline.BuildAssetBundles`, writing straight into
`Assets/K2D2/Copied/assets/bundles/k2d2_ui.bundle` (the exact path
`K2D2_Plugin.cs` already loads from - `AssetsLoader`/`K2D2_Plugin.cs`
need zero changes). Safe to re-run any time UI source changes going
forward, not just once.

**Eighth follow-up - the full dependency closure is now in the project.**
Reese connected `Downloads\k2d2-master\k2d2-master\src\K2D2.Unity\K2D2.Unity\
Assets\Runtime` directly, which cleared the device bridge's 7-folder
staging depth limit that blocked the last 10 files. `Assets/Runtime/K2UI/
USS/` (all 8 stylesheets) and `Assets/Runtime/K2UI/Images/` (`Cross.png`,
`gear.png`) are now copied into K2D2Redux with matching `.meta` files,
byte-size-verified against the source. Every asset `K2D2_Window.uxml` and
its child pages reference by GUID is now present in the project.

**Not yet verified against a real Unity build.** Remaining steps, to be
done in the Editor: run `K2D2 > Rebuild UI Bundle`, watch the Console for
the "marked N UXML/USS asset(s)" / "wrote ... bytes" log lines to confirm
it actually built something, then use Redux SDK's "Test in Game" and check
whether the window opens. If it still doesn't, the `Player.log`-reading
approach from the Fifth follow-up is the way back in - re-check
`K2D2Window.OnUiReload`'s diagnostic logging for whichever line fails
this time.

**Ninth follow-up - real progress: the window opens.** Reese ran `K2D2 >
Rebuild UI Bundle` and tested in-game. The `_rootElement = root[0]`
crash from the Fifth follow-up is gone - the window's `MainFrame` now
renders: title bar, "K2-D2" label, "Reset All Settings" button all
visible on screen. The bundle rebuild fixed the bug it was meant to fix.

A **new, different, non-fatal** problem showed up in its place: two lines
of literal on-screen text reading `Unknown type: 'K2UI.ToggleButton'` and
(partially obscured behind the window's own icon in the screenshot, but
consistent with the same failure mode) another `Unknown type: '...'` for
`K2UI.Tabs.TabbedPage`. This is UI Toolkit's own inline placeholder for a
custom UXML tag it couldn't resolve to a registered element factory at
clone time - it doesn't throw or stop the rest of the tree from building
(which is why the title bar/button around it still render), it just drops
that literal text in where the real control should be. Also present in
the same log dump, all separately confirmed harmless: `Invalid asset path
hint` warnings for `ArialRounded.asset`, `LiberationSans-* SDF.asset`,
`Orbitron-Black SDF.asset`, `Sax Mono.asset` (fonts `K2UI.uss`/
`compas.uss` reference by GUID that weren't part of the copied dependency
closure, since the pages actually read this session don't use them -
only `Caravan.asset`/`Caravan.ttf` were copied; low priority, the rest of
`Assets/Fonts/` from `k2d2-master` can be copied over the same way if
this needs to be fully silenced); two real but pre-existing USS issues in
`compas.uss` itself (`width: 40` missing a unit, `border: 0` - should be
`border-color`) that predate this session's changes and aren't currently
blocking anything visible; and the `last-child` pseudo-class /
Addressables `LegacyResourcesProvider` warnings already confirmed
unrelated Editor/Redux-SDK noise earlier in this document.

Working theory for the "Unknown type" problem, not yet verified: checked
every custom control under `Code/K2UI/` (`ToggleButton.cs`,
`Tabs/TabbedPage.cs`, and everything else - `K2Slider`, `K2Toggle`,
`Group`, `InlineEnum`, `ExFoldoutGroup`, `Labels.cs`'s `Console`/
`StatusLine`, `K2Compas`, `K2ProgressBar`, `K2SliderInt`, `GraphLine`,
`K2AutoFitLabel`, `Tabs/TabsBar.cs`, `Tabs/TabButton.cs`) and every single
one still uses the legacy `public new class UxmlFactory : UxmlFactory<T,
UxmlTraits>` pattern - none use the modern `[UxmlElement]`
attribute-based registration Unity 6's UI Toolkit prefers. That legacy
factory-registration path relies on a one-time reflection scan of loaded
assemblies that's normally driven by the Editor/Player bootstrap - it's
plausible (not yet confirmed) that this scan either runs before a
BepInEx-injected mod assembly like K2D2's is loaded, or doesn't get
triggered for it at all in this Redux/BepInEx runtime context, so its
`IUxmlFactory` types never make it into `VisualElementFactoryRegistry`
even though the assembly itself loads and runs fine (as proven by
everything else in the window working). This lines up directly with
Reese's own earlier priority to "modernize the K2UI controls off
deprecated UxmlTraits" - this may turn out to be the concrete reason that
work needs to happen now rather than staying deferred, rather than being
purely stylistic cleanup.

**Not yet fixed, not yet investigated further** - stopped here for the
night. Next session: confirm the theory (e.g. try converting just one
control, like `ToggleButton`, to `[UxmlElement]`/`[UxmlAttribute]` and
see if that specific "Unknown type" line goes away without touching the
others yet, as a cheap test before committing to converting all of them),
or look for an explicit factory-registration call this project's startup
code could be missing (some Redux/BepInEx UI mods force this via a
`UxmlFactoryRegistry`-touching static constructor or an explicit
`UIElementsUtility`-adjacent bootstrap call in their plugin's `Awake`/
`OnInitialized`).

**Tenth follow-up - all 15 K2UI custom controls converted to
`[UxmlElement]`/`[UxmlAttribute]`, theory from the Ninth follow-up
confirmed by additional evidence and acted on.** Before converting
anything, the theory got stronger evidence from three independent
sources, found by looking at a working sibling Redux mod
(KerbalAutopilot) and decompiling Redux's own UI Toolkit assemblies with
`dnfile`:

- KerbalAutopilot's `MainAppWindow.cs` uses only stock UI Toolkit elements
  (`VisualElement`, `Button`, `Toggle`, `TextField`, `DropdownField`,
  `Label`) and has zero custom controls anywhere - it simply never
  exercises this code path, so it isn't counter-evidence, but it isn't
  supporting evidence either.
- Decompiling `uitkforksp2.controls.Runtime.dll` (Redux's own official
  control library - `AppShell`, `BaseControl`, `ColorPicker`, `OabButton`,
  `Tooltip`, etc.) found every one of Redux's own first-party controls
  uses the modern `UxmlSerializedData`-based pattern - none use legacy
  `UxmlFactory`.
- The same DLL defines `LateBoundUxmlSerializedData` - an explicit
  fallback type for when a serialized custom UXML element's type *can't*
  be resolved at load time, which stores the raw JSON + type name string
  instead of a real typed instance. This is almost certainly the exact
  mechanism producing the "Unknown type: 'X'" placeholder text seen in the
  Ninth follow-up's screenshot.

Converted all 15 controls under `Code/K2UI/`: `ToggleButton`,
`Tabs/TabbedPage`, `Compas/K2Compas` (class `K2Compass`), `Group`,
`InlineEnum`, `K2Slider`, `K2AutoFitLabel`, `GraphLine`, `Labels.cs`
(`Console` + `StatusLine`), `K2Toggle`, `K2ProgressBar`, `ExFoldoutGroup`,
`K2SliderInt`, `Tabs/TabsBar`, `Tabs/TabButton` (`TabPage` + `TabButton`).
Each class is now `partial` with `[UxmlElement]`, the nested
`UxmlFactory`/`UxmlTraits` classes are gone, and every previously
bag-read attribute now sits on its existing property as
`[UxmlAttribute("exact-original-name")]` - the property's own custom
setter logic (side effects, USS class toggling, event firing, etc.) is
completely untouched, only the registration boilerplate changed. Every
attribute name was cross-checked directly against the actual `.uxml`
source files (`K2D2_Window`, `Dock`, `Landing`, `Lift`, `node`,
`StatusGroup`, `about`, `attitude`) rather than trusted from memory,
including `TabbedPage`'s unusually-cased `selected-tab-Name` attribute,
which is preserved exactly.

A few pre-existing default-value mismatches between a C# field's own
initializer and the old `UxmlTraits` bag `defaultValue` were fixed to
match what was actually observed at runtime when loaded from UXML (since
`UxmlTraits.Init()` always ran for anything placed via UXML and
unconditionally overwrote the field with the bag default first, the
field's own initializer was only ever visible for a directly-`new`'d,
non-UXML instance): `K2Compass.AngleRange` (field said `0`, bag said
`90f`), `GraphLine.MaxY` (field said `-1`, bag said `1`),
`K2ProgressBar.Max` (field had no initializer, i.e. `0`; bag said `1`),
and `K2Slider`/`K2SliderInt`'s `labelOnTop` (field said `true`, bag said
`false`). `K2Slider`/`K2SliderInt`'s `Min`/`Max` wrap
`main_slider.lowValue`/`highValue` directly with no backing field of
their own, so those defaults are now set explicitly in the constructor
instead.

Two categories of legacy-only code were deliberately dropped, not
overlooked: `uxmlChildElementsDescription` overrides (restricted which
child types UI Builder would suggest - authoring-time-only, zero runtime
effect, and every UI Toolkit element already derives from `VisualElement`
anyway) on `TabbedPage`, `Group`, `InlineEnum`, `ExFoldoutGroup`, and
`TabsBar`; and explicit "run this once after all attributes are applied"
calls that `Init()` used to make by hand - `K2Compass.UpdateContent()`
and `K2Slider`/`K2SliderInt`'s trailing `SliderValueChanged()`/
`setLabels()` - which are all redundant with a `GeometryChangedEvent`
callback each control's constructor already registers, which fires once
on the element's first real layout after being added to a panel (true for
every element loaded from UXML).

**Not yet done**: this hasn't been tested in-game yet - next step is for
Reese to let Unity recompile (no bundle rebuild needed, since only C#
changed, not the `.uxml`/`.uss` source) and re-test via Redux SDK's Test
in Game, to confirm the "Unknown type" errors are gone.

Separately, this same investigation surfaced something worth revisiting
later, not acted on yet: `KerbalAutopilotMod.cs` loads its window's
`VisualTreeAsset` via `Assets.LoadAssetAsync<VisualTreeAsset>(path)
.WaitForCompletion()` (Addressables), not `AssetBundle.LoadFromFile()`.
That's concrete, already-working evidence that Addressables +
`.WaitForCompletion()` is the real, proven pattern in this ecosystem for
exactly K2D2's situation (a synchronous `VisualTreeAsset` needed before
`Window.Create()`) - which directly undercuts the reasoning recorded in
the Seventh follow-up for deferring Addressables (lack of verified
precedent). Worth reconsidering once the current fix is confirmed
working, as part of "adopt Redux-specific features the original mod never
used."

**Eleventh follow-up - the "Unknown type" theory from the Tenth follow-up was
wrong; found and fixed the real cause.** Reese converted, rebuilt, and
re-tested (confirmed via the Pipeline Log: clean build, zero errors,
staged correctly to `mods/__Testing/K2D2`, fresh Test In Game) - and the
"Unknown type: 'K2UI.ToggleButton'"/'K2UI.Tabs.TabbedPage' errors were
**identical**, completely unaffected by the `[UxmlElement]` conversion.
That ruled out "legacy vs modern attribute style" as the cause and meant
the Tenth follow-up's theory, however well-evidenced it looked at the
time, was wrong.

Decompiled `UnityEngine.UIElementsModule.dll`'s
`VisualElementFactoryRegistry` this time - not just its type/method
*names* like earlier sessions, but its actual IL method bodies (via
`dnfile` + `dncil`, a CIL disassembler from the same team - `pip install
dncil`). `RegisterUserFactories()` (internal, called automatically at
some point during startup) does exactly what its name says: it iterates
`AppDomain.CurrentDomain.GetAssemblies()`, skips any assembly whose name
isn't in `GetAllUserAssemblies()` (also internal - some Unity-internal
notion of "known project assemblies"), then for the ones that pass,
reflects over every type looking for non-abstract, non-generic classes
implementing `IUxmlFactory` and registers them. A BepInEx-injected mod
DLL like K2D2's evidently never appears in `GetAllUserAssemblies()`, no
matter when this scan runs - so K2D2's custom elements were never going
to be found automatically, regardless of which attribute pattern they
used. This is also independently confirmed by a Unity forum thread
(searched this session) describing the exact same "has no registered
factory method" symptom for a custom element shipped in a **precompiled
plugin assembly** specifically, with the same fix applied below.

Also decompiled `RegisterFactory(IUxmlFactory factory)` itself (the
method that actually inserts into the registry's backing dictionary,
keyed by `factory.uxmlQualifiedName` - confirming that key is exactly the
`Namespace.ClassName` string form K2D2's `.uxml` files already use, e.g.
`K2UI.ToggleButton`, with no `xmlns` prefix declarations needed). It's
`protected static`, not `public` - there's no supported public API for a
mod to call it - so reaching it requires reflection.

**The fix**: reverted all 15 K2UI files back to the legacy
`UxmlFactory`/`UxmlTraits` pattern (`git revert` of the Tenth follow-up's
conversion commit - modernizing to `[UxmlElement]` can be revisited later
once there's an equally concrete understanding of *that* pattern's own
registration entry point, but doing it now would just be adding risk with
no proven benefit). Added `KTools/K2UIFactoryRegistration.cs`: builds one
instance of each of K2UI's 17 custom control classes' nested `UxmlFactory`
(across the 15 files - `Labels.cs` and `Tabs/TabButton.cs` each define two
classes), and calls `VisualElementFactoryRegistry.RegisterFactory()` on
each via reflection (`BindingFlags.NonPublic | BindingFlags.Static`).
Wired into `K2D2_Plugin.cs`'s `OnInitialized()`, called right after the
asset bundle loads and before any UXML is loaded, so the registry is
populated before anything tries to resolve `K2UI.*` tags. Registration
failures for individual factories are caught and logged rather than
allowed to crash plugin init, in case this ever runs twice (e.g. a
domain-reload edge case) and hits `RegisterFactory`'s own
duplicate-registration guard.

**Not yet tested in-game** - this is the next thing for Reese to try:
rebuild, Test In Game, and check whether the "Unknown type" text and the
ToggleButton/TabbedPage rendering are both resolved. If this works, the
Unity console should also show a new `K2UIFactoryRegistration: manually
registered 17/17 K2UI custom control factories` log line from `L.Log`,
which is worth checking even if the visual result looks right, since it
confirms the mechanism actually ran rather than something else
coincidentally fixing it.

**Thirteenth follow-up - the real, final root cause, and the actual fix
(this time verified against Unity's own IL, not inferred from a warning
message).** This entry covers two things Reese tested in sequence: a
second attempt at `[UxmlElement]` (which made it into git briefly as a
"Twelfth follow-up" commit before being reverted - it's gone from history
now, folded into this entry instead so there's one coherent account) and
what actually fixed it.

The Eleventh follow-up's factory-registration fix worked exactly as
designed (confirmed via `Ksp2-2.log`: `K2UIFactoryRegistration: manually
registered 17/17...`, no more "Unknown type" errors), but Reese's next
test showed two new problems - no USS styling on any K2UI control, and
clicking any tab (including "About") blanked the whole content area. The
log explained why: every K2UI control logged `Control K2UI.X uses the
deprecated UxmlTraits API. Its attributes were ignored on import`, and
`NodeExUI.onUpdateUI()` NREd every single frame because `panel.Q<K2UI.
Console>("node_infos")` came back null.

Read that warning too literally at first and concluded Unity was skipping
legacy controls' `Init()` entirely, which pointed at `[UxmlElement]` as
the only fix. Reapplied it project-wide (reverting the Eleventh follow-
up's pattern revert) and removed `K2UIFactoryRegistration.cs`, since IL
disassembly of `VisualTreeAsset.Create()` had shown modern-pattern
controls bypass `VisualElementFactoryRegistry` entirely - their type and
attribute data comes from `UxmlSerializedData` baked into the compiled
`VisualTreeAsset` at Editor import time, via `CreateInstance()` +
`Deserialize()`, no runtime assembly scan involved. Reese reimported the
UXML, rebuilt, redeployed, and tested - and this time **no window opened
at all**. The log showed why: `Unknown managed type referenced: K2D2
K2UI.Tabs.TabPage/UxmlSerializedData` (and the same for `TabbedPage` and
`StatusLine`) - confirmed present, correctly nested, and marked
`[Serializable]` in the actually-deployed `K2D2.dll` (checked directly via
`dnfile`, ruling out a source-generator/build-pipeline mismatch as the
cause). So the type is real and correctly built, but Unity's native
managed-type-reference resolution - the same kind of mechanism used for
`[SerializeReference]` fields - still can't find it for a BepInEx-injected
assembly at runtime. This looks like the same category of problem as the
Eleventh follow-up's `GetAllUserAssemblies()` finding, just in a different
internal system, and with no known reflection-based workaround (unlike
`VisualElementFactoryRegistry.RegisterFactory()`, there's no public entry
point to manually register a type into whatever native table this is).
**Conclusion: `[UxmlElement]`/`UxmlSerializedData` cannot be used for
custom controls loaded from an AssetBundle in this modding environment,
full stop - don't try this a third time.** Reverted back to the Eleventh
follow-up's state (legacy `UxmlTraits` + `K2UIFactoryRegistration.cs`).

With modern pattern ruled out for good, went back to the "attributes were
ignored" warning and actually verified what it means at the IL level
instead of trusting its wording. Disassembled `UnityEngine.UIElements.
UxmlFactory<T,U>.Create()` (`dnfile`+`dncil` again) - it unconditionally
does `var instance = new T(); m_Traits.Init(instance, bag, cc); return
instance;`. No version check, no skip - **`Init()` genuinely runs every
time**, including each control's own override. So custom attributes (a
slider's `min`/`max`, a tab's `selected-tab-Name`, etc.) were never
actually broken. Then disassembled the specific `Init()` that gets called
first via `base.Init(ve, bag, cc)` - the true root `UxmlTraits.Init()` in
`UnityEngine.UIElementsModule.dll` - and its entire body is:

```
Debug.LogWarningFormat("Control {0} uses the deprecated UxmlTraits API. "
    + "Its attributes were ignored on import, ...", ve.GetType().FullName);
return;
```

That's it. In this Unity version, the base `VisualElement.UxmlTraits.Init()`
that's supposed to apply the built-in attributes (`name`, `class`,
`tooltip`, etc.) has been gutted down to a warning stub - it does nothing
else. Every one of K2D2's custom controls calls `base.Init(ve, bag, cc)`
at the top of its own override and then keeps going, so their own custom
attributes still apply correctly after that call returns - only the base
attributes are silently lost. `class` turned out to be safe separately -
`VisualTreeAsset.Create()`'s IL calls `AssignClassListFromAssetToElement()`
unconditionally, outside the factory entirely, for every element
regardless of pattern. `name` gets no such treatment, and K2D2's UI leans
on it heavily: `TabbedPage.ShowContent()`'s `page.Show(page.name == code)`,
`K2Page.Init()`'s `panels.Q<TabPage>(code)` / `buttons.Q<TabButton>(code)`,
`TabPage.setButton()`'s `bt.name = name` (which is *also* how a
`TabButton`'s name ends up wrong - it's copied from its `TabPage`), and
direct lookups like `NodeExUI`'s `panel.Q<K2UI.Console>("node_infos")`
scattered across the various `*UI.cs` files. With `name` never set, every
one of those either NREs or silently fails to match - which is the whole
observed bug in one sentence.

**The fix**: every one of K2D2's 17 K2UI custom control classes now
declares its own `UxmlStringAttributeDescription` for `"name"` and applies
it by hand (`ve.name = m_Name.GetValueFromBag(bag, cc);`) right after the
now-inert `base.Init(ve, bag, cc)` call - two of them (`K2UI.Console`,
`K2Toggle`) didn't have an `Init()` override at all before and needed one
added purely for this. This re-derives exactly what the base class used
to do, from the same attribute bag `Init()` already receives, without
depending on Unity's broken implementation at all. No pattern change, no
registry workaround needed this time - `K2UIFactoryRegistration.cs` and
the legacy `UxmlFactory`/`UxmlTraits` pattern both stay exactly as the
Eleventh follow-up left them.

**Confirmed fixed.** Reese redeployed and tested: full styling is back on
every tab (Node, Lift, Landing, Docking, Attitude all render with the
correct dark panel/border chrome, sliders, and graphs), and switching
between tabs works correctly. This closes out the K2UI rendering saga
that ran from the Ninth through Thirteenth follow-ups - `K2UIFactoryRegistration:
manually registered 17/17...` still appears (unchanged from the Eleventh
follow-up), and the `deprecated UxmlTraits API` warnings still print
(that part of the message was accurate and isn't going away without a
full Unity-side fix) but no longer correspond to any actual missing
`name` values.

### Fourteenth follow-up - Lift autopilot InvalidCastException

With the UI itself finally working, Reese tried the Lift autopilot and it
"didn't really work". `Ksp2-3.log` (staged and grepped directly, same as
every other investigation in this file) showed the real problem: not a UI
bug at all, but a crash spamming every single frame the autopilot ran -

```
[EXC 18:09:04.750] InvalidCastException: Specified cast is not valid.
	K2D2.Lift.Ascent.computeValues (System.Boolean compute_delta_ap_per_second)
	K2D2.Lift.LiftPilot.Update ()
	K2D2.Controller.PilotsManager.UpdateControllers ()
	K2D2.K2D2_Plugin.Update ()
```

`Ascent.computeValues()` opens with a cast that a previous follow-up in
this file had marked "VERIFIED":

```csharp
PatchedConicsOrbit orbit = (PatchedConicsOrbit)current_vessel.VesselComponent.Orbit;
```

That verification wasn't wrong in general, but it wasn't complete. Went
back to `dnfile`/metadata inspection (same approach as the Thirteenth
follow-up's IL work) of the real `Assembly-CSharp.dll`, this time reading
the `InterfaceImpl` table instead of disassembling method bodies: searched
for every `TypeDef` that implements `KSP.Sim.IKeplerPatch` (the interface
`VesselComponent.Orbit` is actually declared to return - confirmed by
decoding the `Orbit` property's signature blob by hand). There are exactly
two:

- `KSP.Sim.impl.PatchedConicsOrbit` - the classic type, the one every
  existing cast in this codebase assumed.
- `Redux.Ecs.Components.CurrentPatchedConicsOrbit` - a Redux ECS
  component. Both classes extend `System.Object` directly; neither
  derives from the other, so a `PatchedConicsOrbit` instance and a
  `CurrentPatchedConicsOrbit` instance are completely unrelated at the
  CLR level even though both satisfy `IKeplerPatch`.

Redux's ECS layer hands back a `CurrentPatchedConicsOrbit` for the vessel
that's actively being simulated/flown - which is exactly the vessel the
Lift autopilot is flying, every single `Update()`. The hard cast to
`PatchedConicsOrbit` was therefore guaranteed to throw for this specific
use case, even though the identical-looking cast is genuinely fine
elsewhere in the codebase for orbits that aren't the live, currently-
controlled vessel.

**The fix**: stop casting to the concrete class at all. `Ascent.computeValues()`
only ever reads `orbit.Apoapsis` and `orbit.referenceBody.radius` off the
result, and both are members of `KSP.Sim.IOrbit`, which `IKeplerPatch`
extends - so the interface `VesselComponent.Orbit` already returns has
everything this method needs. Changed the local variable's declared type
from `PatchedConicsOrbit` to `IKeplerPatch` and dropped the cast entirely.
Applied the same fix to `Pilots/Lift/Controlers/Final.cs`'s `getOrbit()` -
the very next step in the Lift pilot pipeline after Ascent (Circularize),
which had the identical cast and would have hit the identical crash the
moment Reese got that far - it only reads `orbit.TimeToAp`, also a member
of `IKeplerPatch`, so the same interface-only fix applies cleanly there.

**Left alone, flagged as a risk**: `Pilots/Landing/LandingPilot.cs` has
the same `(PatchedConicsOrbit)current_vessel.VesselComponent.Orbit` cast
in two places (`computeValues()`, `compute_real_collision()`), and it's
plausible the Landing autopilot will hit the exact same
`InvalidCastException` for the exact same reason once it's actually
tested against the live vessel. It wasn't fixed here because
`compute_real_collision()` also calls `orbit.GetStateVectorsFromUT(...)`,
which is a method defined directly on the concrete `PatchedConicsOrbit`
class - it's not part of `IOrbit`/`IKeplerPatch` or any other interface,
so the same "just widen to the interface" fix doesn't apply there without
more design work (e.g. an `as`-cast with a graceful fallback, or
reimplementing that calculation against interface-only members). Not
touched speculatively since Landing hasn't actually been reported broken
yet - noting it here so the next follow-up isn't starting from zero if it
is.

`FlightPlanCall.cs` and `ManeuverCreator.cs` have similar-looking casts
too, but both are part of the Node/maneuver-node system that's explicitly
out of scope (Flight Plan integration was never asked for) and neither
has been reported broken - left untouched.

**First test round**: Reese redeployed and tried Node, Lift, and Landing.
Node worked flawlessly (it doesn't touch the Ascent/Landing code paths at
all, so this didn't confirm or deny the fix). Lift still didn't do
anything - the UI panel showed every value stuck at `0.00`, exactly as if
`computeValues()` was still returning immediately. Landing showed "No
Collision Detected" even with an actual collision clearly imminent in the
next screenshot.

Went back to the logs (`Ksp2-2.log`, freshest by mtime) rather than
guessing, and found `Ascent.computeValues()` was still throwing the exact
same `InvalidCastException`, thousands of times, well after the fix had
been delivered and (per the mod DLL's own file timestamp) rebuilt. To
settle it definitively, staged the actual deployed
`mods/__Testing/K2D2/K2D2.dll` and disassembled `Ascent.computeValues()`
directly with the same `dnfile`-based tooling used throughout this file -
and its IL still had `castclass KSP.Sim.impl.PatchedConicsOrbit` sitting
right there at `IL_8bd6`, completely unchanged from before this follow-up.
**The fix in this repo's source is correct; it simply never made it into
the DLL Reese tested.** The mod's build (Unity/ThunderKit project) has a
newer file timestamp than the source push, but whatever ran didn't
actually recompile `Ascent.cs` with the new content - most likely a stale
`Library/ScriptAssemblies` cache or a build kicked off before Unity's
editor picked up the file change on disk. Reese needs to force a real
recompile (e.g. close and reopen the Unity project, or otherwise clear
the script-assembly cache) before rebuilding and redeploying, then retest.

While digging through this same log, `K2D2.Landing.LandingPilot.computeValues()`
turned out to be crashing with the identical `InvalidCastException`,
confirming the risk flagged above - and since that crash happened at the
very first line of `computeValues()`, `compute_real_collision()` was never
even being called, which is exactly why "No Collision Detected" always
showed regardless of the actual situation. With actual evidence in hand
instead of just a plausible risk, went ahead and fixed `LandingPilot.cs`
properly this time, in two parts:

- `computeValues()`'s cast: identical situation to `Ascent.cs` - only
  `GetOrbitalVelocityAtUTZup()` is used off `orbit`, which is a member of
  `IOrbit`/`IKeplerPatch`, so the same "widen to the interface, drop the
  cast" fix applies cleanly.
- `compute_real_collision()`'s cast: this one is a genuine partial fix,
  not a full one. `GetStateVectorsFromUT()` really is concrete-only (see
  above), and disassembling it showed it's not a trivial wrapper either -
  it goes through `UniverseModel.ZupAtUT()` and an internal orbit-data
  struct method to produce a `KSP.Sim.Vector` with its own
  `coordinateSystem`, which is what `compute_real_collision()`'s
  `Position ps = new Position(ve.coordinateSystem, pos)` line depends on.
  `IOrbit.GetOrbitalStateVectorsAtUT()` exists on the interface and *is*
  implemented by both orbit types, but its two out-parameters are named
  `localPositionZup`/`relativeVelocityZup` and typed as plain `Vector3d`
  with no attached coordinate system - swapping it in without actually
  working out that Zup transform would trade a loud crash for a silently
  wrong collision prediction, which is worse. Used a safe `as`-cast
  instead (the same defensive pattern `ManeuverCreator.GetLastOrbit()`
  already uses elsewhere in this codebase): when the concrete
  `PatchedConicsOrbit` isn't available, `compute_real_collision()` now
  returns `false` instead of throwing. This stops the crash - which was
  itself blocking `speed_collision`/`burn_duration`/`compute_startBurn()`
  from ever running - but real terrain-collision prediction stays dark
  specifically while Redux is using its ECS "current" orbit
  representation (which, going by the Ascent evidence, may be all of the
  time for the actively-flown vessel). A full fix needs someone to work
  out that Zup coordinate transform properly - flagging it here rather
  than guessing at it.

**Not yet tested in-game.** Reese needs to force a genuinely clean
rebuild this time (see above), redeploy, and try Lift and Landing again.
Expect: Lift should now hold/throttle to its target apoapsis correctly.
Landing should stop showing the constant crash and its speed_collision/
burn_duration numbers should populate, but "No Collision Detected" may
still be wrong in some cases until the Zup transform work above is done -
worth specifically checking whether collision detection now fires
correctly or is just quietly never triggering.

### Fifteenth follow-up - wrong deploy path, real Landing fix, Circularize investigation

Two things going on this round: a process bug on my end that explained why
Lift still didn't work after the Fourteenth follow-up, and follow-through
on the Landing partial fix once test results showed it wasn't enough.

**The Lift fix genuinely wasn't reaching the game at all.** Reese's
project has two separate copies of the source tree on disk:
`K2D2Redux/Source/...` (a plain folder at the project root, outside
Unity's project structure) and `K2D2Redux/Assets/K2D2/Code/...` (the copy
actually inside the Unity project - the only one Unity's compiler ever
looks at). Every fix pushed to the device this follow-up had been landing
in `Source/...`, which Unity was never watching, so no amount of
rebuilding in Unity could have picked them up - it wasn't a stale-cache
problem at all, the files just weren't where Unity could see them.
Confirmed this by pulling both copies directly off the device and diffing
them: `Assets/K2D2/Code/Pilots/Lift/Controlers/Ascent.cs` still had the
untouched original cast, sitting there completely unaffected by any of
this follow-up's pushes. Re-sent the same fixed files to the correct
`Assets/K2D2/Code/...` paths this time, and confirmed the size on disk
matched what was actually pushed. Reese rebuilt and **Lift worked
correctly** - the ascent held its target apoapsis and throttled properly
all the way to orbit.

**Landing's collision detection was still broken after the "partial fix" -
confirmed by testing, not just predicted.** Went back to find a real fix
instead of the defensive fallback. The reason `compute_real_collision()`
needed a concrete `PatchedConicsOrbit` at all turned out to be narrower
than it first looked: `GetStateVectorsFromUT()` was only ever being used
to get a raw position vector plus a velocity vector whose sole purpose was
supplying its `.coordinateSystem` to build a `Position` - the velocity
value itself was discarded. IL inspection of `Assembly-CSharp.dll` turned
up `IOrbit.GetTruePositionAtUT(double)` - an overload that returns a fully
resolved `KSP.Sim.Position` directly, already correctly transformed
internally by the same code paths `GetStateVectorsFromUT` uses (confirmed
by disassembling both - `GetTruePositionAtUT` isn't a naive equivalent,
it goes through the same `ZupAtUT`/orbit-data-struct machinery, just
returns the packaged result instead of raw components). Since it's a
member of `IOrbit` (which `IKeplerPatch` extends, implemented by both
`PatchedConicsOrbit` and Redux's ECS `CurrentPatchedConicsOrbit`), this
works for the live vessel with no concrete cast, no manual Zup transform,
and no defensive fallback needed - simplified `compute_real_collision()`
to call it directly. This should be a full, not partial, fix. **Not yet
retested** - Reese was testing Lift and Circularize at the same time this
came up, so this needs its own dedicated check next.

**Investigated wiring the Lift "Final" stage's `create_ap`/`create_now`
buttons to real circularization, and stopped short of shipping it.**
Those buttons currently do nothing because `createApNode()`/`createNowNode()`
in `Final.cs` are exactly what they look like - fully commented-out stubs
waiting on `K2D2OtherModsInterface.instance.Circularize(...)`, i.e. Flight
Plan, which is explicitly out of scope. The natural non-Flight-Plan
alternative already exists in this codebase -
`KSPService/ManeuverCreator/ManeuverCreator.cs`'s `CircularizeOrbitApoapsis()`/
`CircularizeOrbitPeriapsis()` - so before wiring anything up, checked
whether that code path would actually work for the live vessel. It
wouldn't, for two stacked reasons:

- `CreateManeuverNode_Co()` (the method that actually builds the node)
  has the exact same `(PatchedConicsOrbit)_vesselComponent.Orbit` hard
  cast as everywhere else fixed this follow-up, so it would throw the
  identical `InvalidCastException` for the actively-flown vessel.
- Even fixing that cast wouldn't be enough: `ManeuverNodeData.SetManeuverState()`
  - the actual call that registers the node - only has one overload, and
  it demands the concrete `PatchedConicsOrbit` class specifically (checked
  via `Assembly-CSharp.dll`'s method signature table - there's no
  interface-typed overload at all). Unlike every cast fixed so far in
  this follow-up, there's no "widen to the interface" option here - the
  API itself won't accept anything but the real class. Whether an actual
  `PatchedConicsOrbit` instance is ever obtainable for a vessel currently
  being flown under Redux's ECS orbit model is an open question - it may
  require going through `ManeuverPlanSolver` (which separately exposes
  both a `PatchedConicsList` and a `PatchedNBodyList`, and a
  `GetOrbitalElements()` method that looks like it might convert between
  the two representations) rather than `VesselComponent.Orbit` at all.

This is a real, separate research task, not a quick follow-on to the
casting fixes above - flagging it here rather than guessing at a fix that
would likely fail the same way the last two partial attempts did.

### Sixteenth follow-up - Landing UI froze once collision detection actually worked

Reese confirmed the Fifteenth follow-up's `GetTruePositionAtUT()` fix
worked - Landing autopilot is genuinely flying the descent correctly now.
Two smaller UI bugs surfaced once it did, both traced to the same line in
`LandingUI.cs`.

**"No Collision detected" stays on screen even while landing is clearly
working, and the Touch Down button's highlight never moves off Brake.**
`onUpdateUI()` had `if (!pilot.collision_detected) return true;` right
after `status_bar.Reset()` - which bails out of the *entire rest of the
method*, including `touch_down.Show(pilot.mode != LandingPilot.Mode.TouchDown)`
and the whole Brake!/Touch Down.../Waiting/Pause status-text switch,
whenever `collision_detected` is false. Before this session's fixes that
was a no-op either way, since `compute_real_collision()` never got to run
at all - but now that it actually works, `collision_detected` correctly
flips back to `false` mid-descent (once braking has changed the
trajectory enough that the short forward-projected window no longer
predicts an impact within it), which freezes the touch_down button's
visibility and the mode status text at whatever they were the last time a
collision happened to be predicted - exactly matching both symptoms
reported. Collision *prediction* is only relevant to the info panel
(`updateContext()`, which already checks `pilot.collision_detected` on
its own, correctly, and is unaffected by this) - it has nothing to do
with whether the pilot's current mode should be displayed. Removed the
early return; the game-state check right after it (`state != GameState.FlightView`)
now runs unconditionally instead, which is what it should have been doing
regardless of collision prediction anyway.

**Not yet tested in-game.** This is a small, well-isolated UI-only change
with no orbit/cast risk - low-risk relative to everything else this
session, but still worth Reese confirming the Touch Down highlight now
tracks correctly through a full Brake -> Touch Down transition.

### Docks / Landing / Lift / Nodes / Staging+Drone - full verification pass

Following up on the live-build-error fixes above, the remaining Pilots/
subsystems (`Docks/`, `Landing/`, `Lift/`, `Nodes/`, `StagingPilot.cs` +
`DronePilot.cs`) were each verified in full against the real assemblies
(TypeDef/MethodDef/FieldDef metadata, via `dnfile` - see below). One real
bug was found and fixed; two more were found and deliberately left alone
as out of scope for a Redux-API-correctness pass (they're pre-existing
logic bugs, not porting breakage):

- **Fixed** - `Pilots/Docks/SelectTargetUI.cs`, `buildControlList()` and
  `buildTargetList()`: both built a `List<string>` via
  `part_names.Append(...)` / `vessel_names.Append(...)` inside a `foreach`,
  with `using System.Linq;` in scope. `List<T>` has no mutating `Append` -
  this silently resolved to the LINQ extension `Enumerable.Append<T>()`,
  which returns a *new* sequence and doesn't mutate the list, leaving both
  lists permanently empty and both dropdowns permanently unpopulated.
  Fixed to `.Add(...)` at both sites.
- **Fixed** - `Pilots/Docks/SelectTargetUI.cs`, `buildTargetList()`: after
  building `vessel_names`, the result was assigned to
  `control_from_drop.choices` instead of `target_drop.choices` (apparent
  copy-paste from `buildControlList()` above it) - the target dropdown
  never actually got populated with vessel names, and the control dropdown
  would get clobbered with vessel names instead of part names whenever the
  target dropdown was opened. Corrected to assign into `target_drop`. Also
  removed a redundant duplicate line (`allVessels = ...GetAllVessels();`
  was called twice in a row) just above it.
- **Fixed** - `DronePilot.cs`'s `DroneSettings`: `wanted_altitude` was
  bound to the same settings key (`"drone.wanted_speed"`) as
  `wanted_speed`, so the two values aliased each other's persisted setting
  instead of being stored independently - saving one silently overwrote the
  other. The commented-out UI code further down the file
  (`AltitudeControl("drone.wanted_altitude", ...)`) confirmed the intended
  key; changed to `"drone.wanted_altitude"`.
- **Verified clean, no fixes needed** - `Landing/`, `Lift/`, `Nodes/`,
  `StagingPilot.cs`: every external call matched the current assemblies
  exactly (beyond the `IKeplerPatch` casts already fixed above). One item
  in `Nodes/NodeExPilot.cs` (line 229, `UnityEngine.Input.GetKey`/
  `GetKeyDown`) could not be fully verified - its defining module,
  `UnityEngine.InputLegacyModule.dll`, isn't present in the local
  assembly snapshot - so it was left untouched rather than guessed at.

This pass also moved verification onto a more rigorous footing: instead of
`monodis` (which reliably crashes partway through a full `--method` dump of
`Assembly-CSharp.dll` - confirmed again this session, at method-table row
~60742 of ~73920, roughly 82% through), verification now uses `dnfile` (a
pure-Python ECMA-335 metadata parser, `pip install dnfile`), including
custom signature-blob decoding to resolve full method signatures (return
and parameter types), not just member names.

With this pass done, every Pilots/ subsystem has now been verified at
least once against the real assemblies. K2UI/ remains the only
unverified area of Source/.

## Flight Plan (not yet started)

K2-D2 optionally integrates with **Flight Plan** by schlosrat -
https://github.com/schlosrat/FlightPlan - licensed under **GNU GPL v3**
(its own `license.md`), and itself vendoring the ALGLIB numerical library
under GPL v2. Porting Flight Plan to Redux is a separate, not-yet-started
effort (Flight Plan also hard-depends on a third mod, Node Manager, which
has its own porting/compatibility question). Given the GPL v3 license is a
stronger copyleft than this project's CC-BY-SA 4.0, Flight Plan should be
ported as its own separately-licensed project rather than merged into this
one's codebase - not a decision to make casually or reverse later.
