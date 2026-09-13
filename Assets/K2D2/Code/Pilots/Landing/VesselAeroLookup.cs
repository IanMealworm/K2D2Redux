using System;
using System.Collections.Generic;
using KSP.Modules;
using KSP.Sim.impl;
using UnityEngine;
// L (see K2D2_Plugin.cs) lives in the K2D2 namespace, not a using - it resolves here for free
// since K2D2.Landing nests under K2D2, same as DocksTools.cs's own unqualified L.Log calls.

namespace K2D2.Landing
{
    // Per-part aerodynamic drag/lift estimation, reproducing the game's own real force-application
    // formulas rather than approximating them, so results are directly comparable to the vessel's
    // own measured acceleration.
    //
    // Drag: Module_Drag.UpdateDrag's real formula (from disassembly) is:
    //
    //   dragScalar = cubeData.AreaDrag
    //              * PhysicsSettings.DragCubeMultiplier
    //              * PhysicsSettings.DragCurvePseudoReynolds.Evaluate(part.atmDensity * part.VelocityRelativeToSOIBodyFrameInPhysicsSpace.magnitude)
    //              * PhysicsSettings.DragMultiplier
    //              * part.dynamicPressure
    //              * 0.001f
    //
    // cubeData comes from Data_Drag.GetAeroDataForDirection(direction, mach, ref cube), called
    // with the part's own local-space velocity direction (PartBehavior.ModelTransform.
    // InverseTransformDirection of the part's world velocity) and PartBehavior.machNumber - the
    // same direction/speed the game's own single real caller (Module_Drag.OnModuleFixedUpdate ->
    // UpdateCubeData -> GetAeroDataForDirection) uses. part.dynamicPressure is read directly off
    // PartBehavior (already in Pa, straight from the physics engine), not hand-computed from
    // 0.5*density*speed^2 - the game's own number does not match that textbook formula exactly.
    // DragCurvePseudoReynolds is evaluated against density*speed (not squared) - a separate curve
    // from both Mach and dynamic pressure. PhysicsSettings.DragCubeMultiplier/DragMultiplier are
    // two more real multipliers that must be applied. All of PhysicsSettings' relevant members are
    // public static on a top-level class, so this calls the same live objects the game itself
    // evaluates every physics tick. The *0.001 puts the result in kN, matching the kN-thrust/
    // tonnes-mass convention used elsewhere in this codebase (e.g. BurndV.cs), so dividing by
    // vessel mass needs no extra conversion.
    //
    // Body lift: Module_Drag.UpdateBodyLift's real formula:
    //
    //   bodyLiftScalar = part.dynamicPressurekPa                              // PartComponent's own
    //                   * dataDrag.bodyLiftMultiplier                         // kPa property, NOT
    //                   * PhysicsSettings.BodyLiftMultiplier                  // PartBehavior's Pa-
    //                   * PhysicsSettings.BodyLiftLiftingSurfaceCurve         // scaled one - no
    //                        .liftMachCurve.Evaluate(part.machNumber)         // *0.001 needed here
    //
    //   bodyLiftForceRaw = bodyLiftScalar * cubeData.LiftForce
    //   liftForce = Vector3.ProjectOnPlane(bodyLiftForceRaw, direction)   // strips the component
    //                                                                      // along the velocity
    //                                                                      // direction, keeping
    //                                                                      // only the genuinely
    //                                                                      // perpendicular part
    //
    // ProjectOnPlane's result doesn't depend on the plane normal's sign, so this file passes plain
    // +direction, not -direction - same answer, one fewer negate. Lift direction genuinely differs
    // per part (unlike drag's scalar sum), so it's accumulated as an actual Vector3, converted out
    // of each part's own local space into world space (ModelTransform.TransformDirection) before
    // summing, so parts facing different ways add up correctly instead of cancelling.
    //
    // PartComponent (sim-side, from PartOwner.Parts) and PartBehavior (view-side, from
    // vesselBehavior.parts) are two different objects for the same physical part; Guid is the link
    // between them (PartComponent's is a plain string via its ObjectComponent base, PartBehavior's
    // is a small IGGuid struct, hence the .ToString() below).
    //
    // NOT covered: Module_LiftingSurface (wings) and its subclass Module_ControlSurface (grid
    // fins, control surfaces) go through an entirely separate OnModuleFixedUpdate ->
    // CalculateLiftDragForces -> GetLiftVector/GetDragVector pipeline, not Data_Drag/Module_Drag -
    // so a vessel relying on those for its aerodynamic force is missing that contribution here.
    // Module_ControlSurface exposes GetPotentialLift/GetPotentialTorque (confirmed via disassembly
    // to compute "what lift would this surface produce fully deflected"), a plausible building
    // block for a future closed-loop steering-authority estimate, but CalculateLiftDragForces/
    // GetLiftVector themselves are not yet verified against the compiled assembly.
    public static class VesselAeroLookup
    {
        public struct VesselAeroSum
        {
            public bool found;
            public double totalForceEstimate;      // sum, over every part, of that part's real
                                                    // dragScalar - see the class comment above for
                                                    // the exact formula, straight from
                                                    // Module_Drag.UpdateDrag's own disassembly.
                                                    // Should be directly comparable to
                                                    // compute_atmo_collision's dragAccel once
                                                    // divided by vessel mass - no unit conversion
                                                    // of our own layered on top.
            public double totalAreaDrag;           // sum of each part's Data_Drag.CubeData.AreaDrag,
                                                    // kept only for comparison against older
                                                    // versions of this estimate.
            public double totalCrossSectionalArea; // sum of Data_Drag.CubeData.CrossSectionalArea
            public Vector3 totalLiftForceEstimate; // vector sum (world space) of every part's real
                                                    // body-lift force - see the class comment above
                                                    // for the exact formula, straight from
                                                    // Module_Drag.UpdateBodyLift's own disassembly.
                                                    // Does NOT include Module_LiftingSurface/
                                                    // Module_ControlSurface (wings, grid fins,
                                                    // control surfaces) - see class comment, that's
                                                    // a genuinely separate system not covered here yet.
            public int partsEvaluated;
            public int partsLiftEvaluated;         // subset of partsEvaluated whose
                                                    // Data_Drag.BodyLiftEnabled was actually true -
                                                    // drag and lift are independently toggleable per
                                                    // part, so this can be smaller than partsEvaluated
            public int partsSkipped; // no Data_Drag module, no matching PartBehavior found, or
                                      // threw while evaluating

            // DIAGNOSTIC ONLY - captured from the first successfully-evaluated part each call, not
            // summed/averaged across the vessel. Exposes the raw per-part inputs so a caller can
            // tell whether a zero totalForceEstimate/totalLiftForceEstimate reading means "genuinely
            // no force at this altitude/density" or "the curve/table itself returned nothing usable".
            public double sampleDynamicPressure;      // PartBehavior.dynamicPressure (Pa) - the
                                                        // exact value UpdateDrag itself reads
            public double samplePseudoReynolds;        // atmDensity * partSpeed, before the curve
            public float samplePseudoReynoldsFactor;   // PhysicsSettings.DragCurvePseudoReynolds
                                                        // .Evaluate(samplePseudoReynolds) - if THIS
                                                        // is 0 while dynamicPressure isn't, the curve
                                                        // itself is the hard cutoff
            public float sampleMach;
            public double sampleAreaDrag;              // cube.AreaDrag for that same sample part -
                                                        // if this is also ~0, GetAeroDataForDirection
                                                        // itself is returning nothing usable up here

            // Only set by EvaluateVesselAeroAtAttitude (0 otherwise, including for
            // EvaluateVesselAeroReal) - the real angle, in degrees, between the vessel's ACTUAL
            // current ControlTransform.forward and the hypothetical direction being asked about
            // (0 deg = already exactly there, 180 deg = exactly opposite). Needed because a
            // front-to-back ASYMMETRIC vehicle (nose cone on one end, engine bell/fins on the
            // other) can read near-zero real body lift at BOTH 0 deg and 180 deg off retrograde,
            // since lift depends on radial symmetry, not which end faces forward - so "real lift
            // is small" does not reliably mean "real attitude is close to retrograde" for this
            // shape of craft. This field lets a caller check the actual angle directly instead of
            // inferring it from lift magnitude.
            public float attitudeAngleFromHypotheticalDeg;
        }

        // Real (actual current attitude) and hypothetical (a different, commanded attitude - see
        // EvaluateVesselAeroAtAttitude below) queries share every bit of this formula EXCEPT which
        // local direction gets fed into GetAeroDataForDirection. mach, atmDensity, dynamicPressure
        // are all properties of a part's POSITION and SPEED, not which way it's pointed, so none of
        // that changes for a hypothetical-attitude-only query - only directionLocal does. Factored
        // out once here so the two callers can't drift apart on the formula itself.
        //
        // worldFlowDirectionForPart: given a part and its real current velocity direction (world
        // space, already normalized), return the world-space direction that should be treated as
        // "the direction the air is coming from" for THIS query - the real vessel's own velocity
        // direction for EvaluateVesselAeroReal, or that same direction re-expressed under a
        // hypothetical attitude for EvaluateVesselAeroAtAttitude.
        private static VesselAeroSum EvaluateVesselAeroCore(VesselComponent vessel, Func<PartBehavior, Vector3, Vector3> worldFlowDirectionForPart)
        {
            var result = new VesselAeroSum();
            if (vessel == null)
                return result;

            var simObject = vessel.SimulationObject;
            var vesselBehavior = simObject != null ? simObject.objVesselBehavior : null;
            if (vesselBehavior == null)
                return result;

            // PartComponent (sim-side, from PartOwner.Parts below) and PartBehavior (view-side,
            // from vesselBehavior.parts here) are two different objects for the same physical
            // part - see the class comment above. Guid is the only link found between them.
            var behaviorsByGuid = new Dictionary<string, PartBehavior>();
            foreach (var behavior in vesselBehavior.parts)
            {
                if (behavior == null)
                    continue;
                behaviorsByGuid[behavior.Guid.ToString()] = behavior;
            }

            var controlOwner = vessel.GetControlOwner();
            var owner = controlOwner != null ? controlOwner.PartOwner : null;
            if (owner == null)
                return result;

            foreach (var part in owner.Parts)
            {
                if (part == null || part.PartData == null)
                    continue;

                try
                {
                    if (!behaviorsByGuid.TryGetValue(part.Guid, out PartBehavior partBehavior) || partBehavior == null)
                    {
                        result.partsSkipped++;
                        continue;
                    }

                    if (!part.TryGetModuleData<PartComponentModule_Drag, Data_Drag>(out Data_Drag dragData) || dragData == null)
                    {
                        result.partsSkipped++;
                        continue;
                    }

                    Vector3 velocityWorld = partBehavior.VelocityRelativeToSOIBodyFrameInPhysicsSpace;
                    float partSpeed = velocityWorld.magnitude;
                    Vector3 flowDirectionWorld = worldFlowDirectionForPart(partBehavior, velocityWorld.normalized);
                    Vector3 directionLocal = partBehavior.ModelTransform.InverseTransformDirection(flowDirectionWorld);
                    float mach = (float)partBehavior.machNumber;

                    // The real method takes this by ref, not out - its metadata doesn't carry the
                    // [out] marker C# out-params need, even though every field on it does get
                    // written fresh each call. Has to be initialized before the call because of that.
                    var cube = default(Data_Drag.CubeData);
                    dragData.GetAeroDataForDirection(directionLocal, mach, ref cube);

                    // Module_Drag.UpdateDrag's real formula, reproduced term-for-term - see the
                    // class comment above.
                    double pseudoReynolds = partBehavior.atmDensity * partSpeed;
                    float pseudoReynoldsFactor = PhysicsSettings.DragCurvePseudoReynolds.Evaluate((float)pseudoReynolds);

                    double dragScalar = cube.AreaDrag
                        * PhysicsSettings.DragCubeMultiplier
                        * pseudoReynoldsFactor
                        * PhysicsSettings.DragMultiplier
                        * partBehavior.dynamicPressure
                        * 0.001;

                    result.totalForceEstimate += dragScalar;
                    result.totalAreaDrag += cube.AreaDrag;
                    result.totalCrossSectionalArea += cube.CrossSectionalArea;

                    // Diagnostic snapshot - see the VesselAeroSum field comments above for why.
                    // First successfully-evaluated part only, not summed/averaged.
                    if (result.partsEvaluated == 0)
                    {
                        result.sampleDynamicPressure = partBehavior.dynamicPressure;
                        result.samplePseudoReynolds = pseudoReynolds;
                        result.samplePseudoReynoldsFactor = pseudoReynoldsFactor;
                        result.sampleMach = mach;
                        result.sampleAreaDrag = cube.AreaDrag;
                    }

                    // Body lift - see the class comment above for the full derivation
                    // (Module_Drag.UpdateBodyLift's real formula). Independently gated: a part can
                    // have drag enabled and lift disabled (or vice versa), so this isn't folded into
                    // the same "did we get a cube back" check above.
                    if (dragData.BodyLiftEnabled)
                    {
                        float bodyLiftScalar = (float)part.dynamicPressurekPa
                            * dragData.bodyLiftMultiplier
                            * PhysicsSettings.BodyLiftMultiplier
                            * PhysicsSettings.BodyLiftLiftingSurfaceCurve.liftMachCurve.Evaluate(mach);

                        Vector3 bodyLiftForceRaw = bodyLiftScalar * cube.LiftForce;

                        // Vector3.ProjectOnPlane(vector, planeNormal) doesn't depend on the plane
                        // normal's sign - the real code passes -direction, this passes +direction,
                        // same result, see the class comment above.
                        Vector3 liftForceLocal = Vector3.ProjectOnPlane(bodyLiftForceRaw, directionLocal);

                        // Lift direction genuinely differs per part (unlike drag's scalar sum) - has
                        // to be converted out of this part's own local space before summing across
                        // the vessel, or parts facing different ways would silently cancel/reinforce
                        // by accident instead of adding up correctly.
                        Vector3 liftForceWorld = partBehavior.ModelTransform.TransformDirection(liftForceLocal);

                        result.totalLiftForceEstimate += liftForceWorld;
                        result.partsLiftEvaluated++;
                    }

                    result.partsEvaluated++;
                }
                catch (Exception ex)
                {
                    // Defensive - this calls into game internals confirmed only by static
                    // analysis. One misbehaving part shouldn't take down the whole evaluation.
                    L.Log($"[VesselAeroLookup] part {part.Name} threw evaluating real aero: {ex.Message}");
                    result.partsSkipped++;
                }
            }

            result.found = result.partsEvaluated > 0;
            return result;
        }

        // EvaluateVesselAeroCore with "use each part's own real, current velocity direction" as
        // the flow direction - i.e. no attitude hypothesis at all.
        public static VesselAeroSum EvaluateVesselAeroReal(VesselComponent vessel)
        {
            return EvaluateVesselAeroCore(vessel, (partBehavior, realVelocityDirWorld) => realVelocityDirWorld);
        }

        // "What would this vessel's drag/lift be if it were rotated to a DIFFERENT attitude than
        // whatever it's actually doing right now" - the building block FindBestDeorbitBurn needs to
        // plan around a vehicle's drag/lift pointed pure retrograde, without needing to actually be
        // in that attitude yet (it searches burns up to a full orbit ahead of when the vessel would
        // ever fly that way).
        //
        // hypotheticalControlForwardWorld: the world direction we want the vessel's own
        // ControlTransform.forward (VesselBehavior.ControlTransform - a plain Unity Transform,
        // confirmed via disassembly, deliberately chosen over TelemetryComponent's
        // OrbitMovementRetrograde/SurfaceMovementRetrograde properties, which are real navball-
        // backing data but come back as KSP.Sim frame-tagged vectors with no confirmed conversion
        // into the plain Unity world space PartBehavior.ModelTransform already operates in) to
        // hypothetically point. Pass -velocity for "pure retrograde";
        // EvaluateVesselAeroPureRetrograde below is that exact convenience case.
        //
        // How the hypothetical rotation is built: the vessel is treated as one rigid body (this
        // does NOT model individual control-surface deflection - see the class comment's grid-fin
        // note), so re-pointing "the vessel" means applying ONE rotation, attitudeDelta, to every
        // part alike. attitudeDelta is the shortest rotation from "wherever ControlTransform
        // actually points right now" to the hypothetical target direction. Applying
        // Inverse(attitudeDelta) to each part's own real velocity direction before handing it to
        // ModelTransform.InverseTransformDirection is mathematically identical to first rotating
        // every part's world orientation by attitudeDelta and then transforming, without needing
        // to touch any part's rotation directly.
        //
        // Scope: this changes ATTITUDE ONLY, holding the vessel's real current position/speed/
        // density fixed - "what if I re-pointed my nose right now, everything else about this
        // instant unchanged." It does not answer "what would my drag/lift be at some future point
        // along a hypothetical trajectory with different speed/altitude" - that needs bridging
        // this Unity-world-space math with AtmosphericPredictor's Zup/KeplerPropagator coordinate
        // system (see EvaluateVesselAeroPureRetrogradeAtState below for that).
        public static VesselAeroSum EvaluateVesselAeroAtAttitude(VesselComponent vessel, Vector3 hypotheticalControlForwardWorld)
        {
            if (vessel == null || hypotheticalControlForwardWorld.sqrMagnitude < 1e-6f)
                return new VesselAeroSum();

            var simObject = vessel.SimulationObject;
            var vesselBehavior = simObject != null ? simObject.objVesselBehavior : null;
            var controlTransform = vesselBehavior != null ? vesselBehavior.ControlTransform : null;
            if (controlTransform == null)
                return new VesselAeroSum();

            Vector3 hypotheticalNormalized = hypotheticalControlForwardWorld.normalized;
            Quaternion attitudeDelta = Quaternion.FromToRotation(controlTransform.forward, hypotheticalNormalized);
            Quaternion inverseDelta = Quaternion.Inverse(attitudeDelta);

            var result = EvaluateVesselAeroCore(vessel, (partBehavior, realVelocityDirWorld) => inverseDelta * realVelocityDirWorld);
            // See the field's own comment on VesselAeroSum - the real "how far off retrograde is
            // the real attitude right now" number.
            result.attitudeAngleFromHypotheticalDeg = Vector3.Angle(controlTransform.forward, hypotheticalNormalized);
            return result;
        }

        // Convenience wrapper for the specific case FindBestDeorbitBurn actually wants: "pointed
        // pure retrograde." Reference velocity for "which way is retrograde" comes from whichever
        // part happens to be first in vesselBehavior.parts - any part's real current velocity is a
        // fine proxy for "which way is this vessel generally travelling" for the sole purpose of
        // picking a target direction; small part-to-part differences from rotation don't matter
        // here the way they do for the real per-part drag sum itself.
        public static VesselAeroSum EvaluateVesselAeroPureRetrograde(VesselComponent vessel)
        {
            if (vessel == null)
                return new VesselAeroSum();

            var simObject = vessel.SimulationObject;
            var vesselBehavior = simObject != null ? simObject.objVesselBehavior : null;
            if (vesselBehavior == null)
                return new VesselAeroSum();

            PartBehavior referencePart = null;
            foreach (var behavior in vesselBehavior.parts)
            {
                if (behavior == null)
                    continue;
                referencePart = behavior;
                break;
            }

            if (referencePart == null)
                return new VesselAeroSum();

            Vector3 referenceVelocityWorld = referencePart.VelocityRelativeToSOIBodyFrameInPhysicsSpace;
            if (referenceVelocityWorld.sqrMagnitude < 0.01f)
                return new VesselAeroSum(); // too slow for "retrograde" to mean anything stable

            return EvaluateVesselAeroAtAttitude(vessel, -referenceVelocityWorld.normalized);
        }

        // Exposes the exact same "first part's real current velocity" reference every method
        // above builds "pure retrograde" from, so a caller building a fair apples-to-apples
        // comparison against those live-reading methods can feed exactly that speed into
        // EvaluateVesselAeroPureRetrogradeAtState below, instead of a different velocity
        // reference that looks equivalent but isn't:
        // VelocityRelativeToSOIBodyFrameInPhysicsSpace (what every live/real method here uses) is
        // relative to the body's own (rotating) frame, while orbit.GetOrbitalVelocityAtUTZup is
        // inertial - the exact "atmosphere corotation ignored" gap AtmosphericPredictor's class
        // comment flags as simplification #1. Returns 0 if no usable reference part is found.
        public static float GetReferencePartSpeed(VesselComponent vessel)
        {
            if (vessel == null)
                return 0f;

            var simObject = vessel.SimulationObject;
            var vesselBehavior = simObject != null ? simObject.objVesselBehavior : null;
            if (vesselBehavior == null)
                return 0f;

            foreach (var behavior in vesselBehavior.parts)
            {
                if (behavior == null)
                    continue;
                return behavior.VelocityRelativeToSOIBodyFrameInPhysicsSpace.magnitude;
            }

            return 0f;
        }

        // Makes the ENVIRONMENT hypothetical too, on top of EvaluateVesselAeroAtAttitude's
        // hypothetical attitude - "what would this vessel's drag/lift be at some altitude and
        // speed it hasn't actually reached yet" - the piece that lets FindBestDeorbitBurn/
        // AtmosphericPredictor plan a burn without needing a real prior descent's calibration data.
        //
        // dynamicPressure and machNumber read live off a part hard-zero outside the atmosphere -
        // not because the physics is unavailable outside a real flight, but because nothing
        // populates those specific live fields when the vessel isn't sitting in real air (both
        // PartComponent.OnFixedUpdate's fallback branch and TelemetryComponent.
        // UpdateEnvironmentTelemetry - the two real code paths that write them - independently
        // reduce to the same formula, confirmed by disassembly):
        //   dynamicPressure(kPa) = 0.0005 * atmDensity * relativeVelocity^2   (plain textbook
        //                                                                      physics, no hidden
        //                                                                      compressibility
        //                                                                      correction)
        //   soundSpeed = AeroUtilities.GetSoundSpeed(adiabaticIndex, pressure_kPa, density)
        //              = sqrt(adiabaticIndex * 1000 * pressure_kPa / density), or 0 below a tiny
        //                density/pressure floor
        //   mach = soundSpeed > 0 ? relativeVelocity.magnitude / soundSpeed : 0
        //
        // Everything else - the attitude hypothesis (pure retrograde, one rigid-body rotation,
        // same as EvaluateVesselAeroPureRetrograde above), the per-part drag/lift formula itself -
        // is unchanged. Only the SOURCE of dynamicPressure/mach/atmDensity changes: computed once
        // for the whole vessel from (body, altitude, relativeSpeed) instead of read per-part off
        // live telemetry. Deliberately NOT folded into EvaluateVesselAeroCore (which every other
        // entry point above shares) - that shared core's per-part loop is built around reading
        // live PartBehavior properties, so this is self-contained instead, at the cost of the drag/
        // lift formula existing in two places.
        //
        // This method itself is NOT called directly by the per-step integration - it depends on
        // BOTH mach and pseudoReynolds (density*speed) as independent curve inputs, and calling
        // this whole per-part loop fresh at every one of up to 2000 integration steps, for every
        // candidate burn FindBestDeorbitBurn scores, would be real performance trouble (see
        // AtmosphericPredictor's own performance note). Instead, BuildFixedAttitudeDragProfile
        // below samples this same formula at a fixed, small grid of (mach, angle) values once per
        // vessel per burn search, producing a totalAreaDrag-vs-(mach,angle) table.
        // AtmosphericPredictor's profile-based PredictImpact overload then interpolates that table
        // and combines it with pseudoReynoldsFactor/dynamicPressure (cheap closed-form curve/math
        // evaluations that don't need a PartBehavior at all) instead of calling
        // GetAeroDataForDirection per step - AreaDrag is the only piece of the formula that
        // actually depends on a live part object.
        public static VesselAeroSum EvaluateVesselAeroPureRetrogradeAtState(
            VesselComponent vessel, CelestialBodyComponent body, double altitude, double relativeSpeed)
        {
            var result = new VesselAeroSum();
            if (vessel == null || body == null || relativeSpeed < 0.1)
                return result;

            var simObject = vessel.SimulationObject;
            var vesselBehavior = simObject != null ? simObject.objVesselBehavior : null;
            var controlTransform = vesselBehavior != null ? vesselBehavior.ControlTransform : null;
            if (vesselBehavior == null || controlTransform == null)
                return result;

            PartBehavior referencePart = null;
            foreach (var behavior in vesselBehavior.parts)
            {
                if (behavior == null)
                    continue;
                referencePart = behavior;
                break;
            }
            if (referencePart == null)
                return result;

            // Same "which way is retrograde" reference as EvaluateVesselAeroPureRetrograde - real
            // current velocity DIRECTION only, a fine proxy for heading regardless of what
            // relativeSpeed magnitude we're hypothesizing about below.
            Vector3 referenceVelocityWorld = referencePart.VelocityRelativeToSOIBodyFrameInPhysicsSpace;
            if (referenceVelocityWorld.sqrMagnitude < 0.01f)
                return result;

            Vector3 hypotheticalNormalized = (-referenceVelocityWorld).normalized;
            Quaternion attitudeDelta = Quaternion.FromToRotation(controlTransform.forward, hypotheticalNormalized);
            Quaternion inverseDelta = Quaternion.Inverse(attitudeDelta);
            result.attitudeAngleFromHypotheticalDeg = Vector3.Angle(controlTransform.forward, hypotheticalNormalized);

            // Environment - see this method's own comment above for the disassembly-confirmed
            // formula. Computed ONCE for the whole vessel (same "one rigid body, one shared flow
            // condition" simplification the attitude hypothesis already makes above), not per part.
            double pressureKPa = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            double atmDensity = body.GetDensity(pressureKPa, temperature);
            double dynamicPressureKPa = 0.0005 * atmDensity * relativeSpeed * relativeSpeed;
            double dynamicPressurePa = dynamicPressureKPa * 1000.0;
            double soundSpeed = AeroUtilities.GetSoundSpeed(body.atmosphereAdiabaticIndex, pressureKPa, atmDensity);
            float mach = soundSpeed > 0 ? (float)(relativeSpeed / soundSpeed) : 0f;
            double pseudoReynolds = atmDensity * relativeSpeed;
            float pseudoReynoldsFactor = PhysicsSettings.DragCurvePseudoReynolds.Evaluate((float)pseudoReynolds);

            var controlOwner = vessel.GetControlOwner();
            var owner = controlOwner != null ? controlOwner.PartOwner : null;
            if (owner == null)
                return result;

            var behaviorsByGuid = new Dictionary<string, PartBehavior>();
            foreach (var behavior in vesselBehavior.parts)
            {
                if (behavior == null)
                    continue;
                behaviorsByGuid[behavior.Guid.ToString()] = behavior;
            }

            foreach (var part in owner.Parts)
            {
                if (part == null || part.PartData == null)
                    continue;

                try
                {
                    if (!behaviorsByGuid.TryGetValue(part.Guid, out PartBehavior partBehavior) || partBehavior == null)
                    {
                        result.partsSkipped++;
                        continue;
                    }

                    if (!part.TryGetModuleData<PartComponentModule_Drag, Data_Drag>(out Data_Drag dragData) || dragData == null)
                    {
                        result.partsSkipped++;
                        continue;
                    }

                    // Direction hypothesis only - magnitude comes from the shared relativeSpeed
                    // above (that's the whole point: this part hasn't actually reached this speed
                    // yet), same rigid-body rotation trick as EvaluateVesselAeroAtAttitude.
                    Vector3 realVelocityDirWorld = partBehavior.VelocityRelativeToSOIBodyFrameInPhysicsSpace.normalized;
                    Vector3 flowDirectionWorld = inverseDelta * realVelocityDirWorld;
                    Vector3 directionLocal = partBehavior.ModelTransform.InverseTransformDirection(flowDirectionWorld);

                    var cube = default(Data_Drag.CubeData);
                    dragData.GetAeroDataForDirection(directionLocal, mach, ref cube);

                    double dragScalar = cube.AreaDrag
                        * PhysicsSettings.DragCubeMultiplier
                        * pseudoReynoldsFactor
                        * PhysicsSettings.DragMultiplier
                        * dynamicPressurePa
                        * 0.001;

                    result.totalForceEstimate += dragScalar;
                    result.totalAreaDrag += cube.AreaDrag;
                    result.totalCrossSectionalArea += cube.CrossSectionalArea;

                    if (result.partsEvaluated == 0)
                    {
                        result.sampleDynamicPressure = dynamicPressurePa;
                        result.samplePseudoReynolds = pseudoReynolds;
                        result.samplePseudoReynoldsFactor = pseudoReynoldsFactor;
                        result.sampleMach = mach;
                        result.sampleAreaDrag = cube.AreaDrag;
                    }

                    if (dragData.BodyLiftEnabled)
                    {
                        float bodyLiftScalar = (float)dynamicPressureKPa
                            * dragData.bodyLiftMultiplier
                            * PhysicsSettings.BodyLiftMultiplier
                            * PhysicsSettings.BodyLiftLiftingSurfaceCurve.liftMachCurve.Evaluate(mach);

                        Vector3 bodyLiftForceRaw = bodyLiftScalar * cube.LiftForce;
                        Vector3 liftForceLocal = Vector3.ProjectOnPlane(bodyLiftForceRaw, directionLocal);
                        Vector3 liftForceWorld = partBehavior.ModelTransform.TransformDirection(liftForceLocal);

                        result.totalLiftForceEstimate += liftForceWorld;
                        result.partsLiftEvaluated++;
                    }

                    result.partsEvaluated++;
                }
                catch (Exception ex)
                {
                    L.Log($"[VesselAeroLookup] part {part.Name} threw evaluating hypothetical-state aero: {ex.Message}");
                    result.partsSkipped++;
                }
            }

            result.found = result.partsEvaluated > 0;
            return result;
        }

        // A fixed-attitude drag table, sampled once per burn search rather than per integration
        // step. The vessel holds whatever fixed inertial attitude it has when the deorbit burn
        // finishes (pointed retrograde AT THAT MOMENT) - freefall applies no torque to correct it
        // from there - while the velocity vector itself keeps rotating as the flight path angle
        // steepens through the fall. So the angle between "which way the vessel is pointed" and
        // "which way is currently retrograde" grows on its own during the coast, with zero
        // attitude control input, and a model has to account for that growth rather than assuming
        // the vessel stays pinned at a fixed angle off retrograde.
        //
        // This table is therefore 2D (mach, angleOffBurnTimeAttitude), not just mach:
        // AtmosphericPredictor recomputes the actual angle fresh every integration step from how
        // far its own modeled velocity vector has rotated since the top of the prediction.
        //
        // What this does NOT model: which PLANE that rotation happens in relative to the vessel's
        // own body axes (roll around the vessel's long axis is a separate degree of freedom this
        // doesn't track) - reasonable for a roughly axisymmetric booster shape, where "how far off
        // your original heading" matters far more than "which direction you're off in." A vessel
        // shape where that assumption doesn't hold would need this revisited.
        //
        // AreaDrag alone is the only thing worth precomputing (see EvaluateVesselAeroCore's
        // dragScalar term-by-term): it's the only piece that needs a live PartBehavior/Data_Drag
        // call (GetAeroDataForDirection), and it depends on directionLocal (a function of both the
        // assumed angle and mach) - every other term (DragCubeMultiplier/DragMultiplier,
        // pseudoReynoldsFactor, dynamicPressure) is a cheap closed-form function of (density,
        // speed) alone, recombined fresh per step in AtmosphericPredictor rather than baked into
        // this table.
        public struct AeroDragProfile
        {
            public bool found;
            public float[] machSamples;
            public float[] angleSamples; // degrees, 0-180, off the "pure retrograde at the moment
                                          // this profile was built" direction - see
                                          // BuildFixedAttitudeDragProfile's own comment for how a
                                          // 0-180 sweep is actually generated from that one direction.
            public double[,] totalAreaDragAtMachAngle; // [machIndex, angleIndex] - summed
                                                        // cube.AreaDrag over every part, holding the
                                                        // vessel's build-time attitude fixed and
                                                        // rotating the ASSUMED flow angle away from
                                                        // it by angleSamples[angleIndex], sampled at
                                                        // machSamples[machIndex].
            public int partsEvaluated;
            public int partsSkipped;
        }

        // Mach sample points - denser around 0.8-1.2 (transonic), where real measured drag dips
        // then rises, sparser at the high-mach end where a real reentry spends comparatively
        // little time.
        static readonly float[] DefaultDragProfileMachSamples =
        {
            0f, 0.3f, 0.5f, 0.7f, 0.8f, 0.85f, 0.9f, 0.95f, 1.0f, 1.05f, 1.1f, 1.2f, 1.4f,
            1.7f, 2f, 3f, 5f, 8f, 12f, 18f, 25f
        };

        // Angle sample points, uniform across the full 0-180 range - no basis yet for making this
        // non-uniform the way the mach samples are, since which part of 0-180 a given trajectory
        // actually settles into isn't known ahead of the burn/periapsis choice.
        static readonly float[] DefaultDragProfileAngleSamples =
        {
            0f, 15f, 30f, 45f, 60f, 75f, 90f, 105f, 120f, 135f, 150f, 165f, 180f
        };

        // Builds the 2D table above. Same "first part's real current velocity direction" proxy
        // every other method here uses to mean "which way is retrograde right now," but instead of
        // building ONE hypothetical attitude (pure retrograde) and sampling it across mach only,
        // this builds a whole FAMILY of hypothetical attitudes - each angleSamples[i] degrees away
        // from pure retrograde - and samples every one of them across mach.
        //
        // How "X degrees away from pure retrograde" is turned into an actual 3D direction to feed
        // the same rigid-body-rotation trick every other method here uses: pick one arbitrary axis
        // perpendicular to the pure-retrograde direction (arbitrary because the rotation PLANE
        // isn't modeled - see this table's own class comment), then rotate the pure-retrograde
        // direction around that axis by each sampled angle in turn. This sweeps through a single
        // consistent plane rather than an arbitrary new direction per angle sample, keeping the
        // family of hypotheses internally consistent (each one differs from its neighbors by a
        // pure rotation, not an unrelated jump).
        //
        // machSamples/angleSamples: pass null for the Default* arrays above.
        public static AeroDragProfile BuildFixedAttitudeDragProfile(VesselComponent vessel, float[] machSamples = null, float[] angleSamples = null)
        {
            var result = new AeroDragProfile();
            if (vessel == null)
                return result;

            machSamples ??= DefaultDragProfileMachSamples;
            angleSamples ??= DefaultDragProfileAngleSamples;

            var simObject = vessel.SimulationObject;
            var vesselBehavior = simObject != null ? simObject.objVesselBehavior : null;
            var controlTransform = vesselBehavior != null ? vesselBehavior.ControlTransform : null;
            if (vesselBehavior == null || controlTransform == null)
                return result;

            PartBehavior referencePart = null;
            foreach (var behavior in vesselBehavior.parts)
            {
                if (behavior == null)
                    continue;
                referencePart = behavior;
                break;
            }
            if (referencePart == null)
                return result;

            Vector3 referenceVelocityWorld = referencePart.VelocityRelativeToSOIBodyFrameInPhysicsSpace;
            if (referenceVelocityWorld.sqrMagnitude < 0.01f)
                return result; // too slow for "retrograde" to mean anything stable

            Vector3 pureRetrogradeNormalized = (-referenceVelocityWorld).normalized;

            // Arbitrary-but-consistent perpendicular axis to sweep the angle family around - see
            // this method's own comment above. Vector3.up is the natural first choice; falling back
            // to Vector3.right on the (rare) chance pureRetrograde is nearly parallel to Vector3.up
            // (straight up/down relative to the world), where Cross would come back near-zero/
            // numerically unstable otherwise.
            Vector3 auxAxis = Mathf.Abs(Vector3.Dot(pureRetrogradeNormalized, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 sweepAxis = Vector3.Cross(pureRetrogradeNormalized, auxAxis).normalized;

            var controlOwner = vessel.GetControlOwner();
            var owner = controlOwner != null ? controlOwner.PartOwner : null;
            if (owner == null)
                return result;

            var behaviorsByGuid = new Dictionary<string, PartBehavior>();
            foreach (var behavior in vesselBehavior.parts)
            {
                if (behavior == null)
                    continue;
                behaviorsByGuid[behavior.Guid.ToString()] = behavior;
            }

            result.machSamples = machSamples;
            result.angleSamples = angleSamples;
            result.totalAreaDragAtMachAngle = new double[machSamples.Length, angleSamples.Length];

            int partsEvaluated = 0;
            int partsSkipped = 0;

            for (int a = 0; a < angleSamples.Length; a++)
            {
                Vector3 hypotheticalNormalized = Quaternion.AngleAxis(angleSamples[a], sweepAxis) * pureRetrogradeNormalized;
                Quaternion attitudeDelta = Quaternion.FromToRotation(controlTransform.forward, hypotheticalNormalized);
                Quaternion inverseDelta = Quaternion.Inverse(attitudeDelta);

                // directionLocal is fixed per part for a given angle sample (computed once here,
                // reused for every mach sample below) - only the ATTITUDE hypothesis changes
                // directionLocal, and that's fixed once inverseDelta is fixed for this angle.
                var partDirections = new List<(Data_Drag dragData, Vector3 directionLocal)>();
                foreach (var part in owner.Parts)
                {
                    if (part == null || part.PartData == null)
                        continue;

                    try
                    {
                        if (!behaviorsByGuid.TryGetValue(part.Guid, out PartBehavior partBehavior) || partBehavior == null)
                        {
                            if (a == 0) partsSkipped++;
                            continue;
                        }

                        if (!part.TryGetModuleData<PartComponentModule_Drag, Data_Drag>(out Data_Drag dragData) || dragData == null)
                        {
                            if (a == 0) partsSkipped++;
                            continue;
                        }

                        Vector3 realVelocityDirWorld = partBehavior.VelocityRelativeToSOIBodyFrameInPhysicsSpace.normalized;
                        Vector3 flowDirectionWorld = inverseDelta * realVelocityDirWorld;
                        Vector3 directionLocal = partBehavior.ModelTransform.InverseTransformDirection(flowDirectionWorld);
                        partDirections.Add((dragData, directionLocal));
                    }
                    catch (Exception ex)
                    {
                        L.Log($"[VesselAeroLookup] part {part.Name} threw building drag profile direction (angle={angleSamples[a]:n0}): {ex.Message}");
                        if (a == 0) partsSkipped++;
                    }
                }

                if (a == 0)
                    partsEvaluated = partDirections.Count;

                for (int m = 0; m < machSamples.Length; m++)
                {
                    double sumAreaDrag = 0;
                    foreach (var (dragData, directionLocal) in partDirections)
                    {
                        try
                        {
                            var cube = default(Data_Drag.CubeData);
                            dragData.GetAeroDataForDirection(directionLocal, machSamples[m], ref cube);
                            sumAreaDrag += cube.AreaDrag;
                        }
                        catch (Exception ex)
                        {
                            L.Log($"[VesselAeroLookup] part threw sampling drag profile at mach {machSamples[m]:n2} angle {angleSamples[a]:n0}: {ex.Message}");
                        }
                    }

                    result.totalAreaDragAtMachAngle[m, a] = sumAreaDrag;
                }
            }

            if (partsEvaluated == 0)
                return new AeroDragProfile { partsSkipped = partsSkipped };

            result.partsEvaluated = partsEvaluated;
            result.partsSkipped = partsSkipped;
            result.found = true;
            return result;
        }

        // Bilinear interpolation into the 2D table above - mach and angle are independent axes
        // (genuinely different degrees of freedom, one from speed/environment, one from attitude
        // history), so this interpolates each axis independently and combines the result the
        // standard bilinear way. Both axes clamp at their sampled range's edges rather than
        // extrapolating - mach because 0-25 already comfortably covers any realistic Kerbin
        // reentry, angle because 0-180 is the entire physically possible range to begin with (an
        // "angle off" a direction can't exceed 180 by construction). Pure math, no game object
        // access - safe and cheap to call from AtmosphericPredictor's per-step loop.
        public static double SampleDragProfile(AeroDragProfile profile, float mach, float angleDeg)
        {
            if (!profile.found || profile.machSamples == null || profile.angleSamples == null
                || profile.machSamples.Length == 0 || profile.angleSamples.Length == 0)
                return 0;

            int machLo = ClampedLowerIndex(profile.machSamples, mach, out double machT);
            int angleLo = ClampedLowerIndex(profile.angleSamples, angleDeg, out double angleT);
            int machHi = Math.Min(machLo + 1, profile.machSamples.Length - 1);
            int angleHi = Math.Min(angleLo + 1, profile.angleSamples.Length - 1);

            double v00 = profile.totalAreaDragAtMachAngle[machLo, angleLo];
            double v10 = profile.totalAreaDragAtMachAngle[machHi, angleLo];
            double v01 = profile.totalAreaDragAtMachAngle[machLo, angleHi];
            double v11 = profile.totalAreaDragAtMachAngle[machHi, angleHi];

            double vAngleLo = v00 + machT * (v10 - v00);
            double vAngleHi = v01 + machT * (v11 - v01);
            return vAngleLo + angleT * (vAngleHi - vAngleLo);
        }

        // Shared helper for SampleDragProfile above - finds the sample index immediately at or
        // below `value` (clamped so the result plus 1 is always a valid index, i.e. never returns
        // the last index unless value is at or past the top of the range) and the fractional
        // position between that sample and the next, for linear interpolation on one axis.
        static int ClampedLowerIndex(float[] samples, float value, out double t)
        {
            int last = samples.Length - 1;

            if (value <= samples[0] || last == 0)
            {
                t = 0;
                return 0;
            }

            if (value >= samples[last])
            {
                t = 0;
                return last; // ClampedLowerIndex+1 gets clamped back to `last` by the caller
            }

            for (int i = 0; i < last; i++)
            {
                if (value >= samples[i] && value <= samples[i + 1])
                {
                    float span = samples[i + 1] - samples[i];
                    t = span > 1e-6f ? (value - samples[i]) / span : 0;
                    return i;
                }
            }

            t = 0;
            return last; // shouldn't reach here given the clamps above
        }
    }
}
