using System;
using System.Collections.Generic;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing
{
    // Atmospheric precision landing predictor (Kerbin, propulsive-only - no chutes yet). The
    // vacuum version's compute_real_collision() propagates the vessel's unpowered orbit forward
    // and finds where that conic section crosses terrain - valid in a vacuum, but once there's
    // real air in the way that's not the trajectory the vessel actually flies: drag bleeds speed
    // the whole way down, which both slows the fall and pulls the impact point short of where a
    // dragless coast would land.
    //
    // Rather than reimplementing an aerodynamics model from scratch, this reads the game's own
    // per-frame aero state via:
    //   - CelestialBodyComponent.GetPressure/GetTemperature/GetDensity/atmosphereDepth
    //   - VesselComponent.DynamicPressure_kPa / DragCoefficient / MachNumber
    // and scales that forward using the game's own density-vs-altitude curve for the rest of the
    // fall, the same way graviticAcceleration is read straight off the vessel elsewhere in this
    // project instead of computing gravity by hand.
    //
    // NOT WIRED INTO THE AUTOPILOT YET - a standalone predictor, validated by comparing its answer
    // against where a real flight actually lands before it drives any burn timing or targeting.
    //
    // Known simplifications:
    //   1. Atmosphere corotation is ignored - drag is computed against velocity in the same
    //      inertial/orbital frame the orbit propagation uses, not against the moving air (which
    //      corotates with the body). On Kerbin that's up to ~175 m/s at the equator - if predicted
    //      impact is consistently off in the direction of (or against) the body's spin, this is
    //      the first place to look.
    //   2. The ballistic factor (drag-per-unit-density-per-unit-speed-squared) is calibrated ONCE
    //      from the vessel's current state and held constant for the whole prediction. Real drag
    //      coefficient shifts with Mach number through transonic/supersonic regimes, and changes
    //      completely if a chute opens - fine for a propulsive-only pass, not once chutes are
    //      modeled.
    //   3. Integration is semi-implicit Euler, not RK4 - cheap and stable enough for a reasonable
    //      step size, but accumulates more error per step than a higher-order method.
    //   4. PredictImpact fast-forwards through the vacuum portion of the trajectory (above
    //      atmosphereTopRadius) using KeplerPropagator instead of numerically integrating every
    //      step - an EXACT optimization, not an approximation, since there's no drag above
    //      atmosphereTopRadius in this model to begin with.
    //
    // Performance: do NOT call this every Update() frame at full step count - this is several
    // hundred to a few thousand terrain/atmosphere queries per call. Call it at a deliberate
    // cadence (a timer, or specific mode transitions) once it's wired into anything continuous.
    public static class AtmosphericPredictor
    {
        // The angle-off-retrograde the profile-based PredictImpact overloads below assume for the
        // entire atmospheric portion of a prediction - see VesselAeroLookup.AeroDragProfile's own
        // comment for the flight-test history behind this constant. If a different vessel shape
        // settles at a different angle, this is the one number to recalibrate.
        public const float AssumedAngleOffRetrogradeDeg = 90f;

        public struct Result
        {
            public bool found;
            public double impactUT;
            public double impactLat;
            public double impactLon;
            public double impactSpeed;
            public int stepsTaken;

            // Only populated when a caller passes captureTrace: true to one of the dragProfile-
            // based PredictImpact overloads below - null otherwise, so every other caller (the
            // 36-candidate search included) pays nothing extra. Lets a caller re-run PredictImpact
            // for one specific trajectory and get back speed/altitude/mach waypoints comparable to
            // a real logged descent.
            public List<Waypoint> trace;
        }

        // One point along a captured trace - see Result.trace above.
        public struct Waypoint
        {
            public double ut;
            public double altitude;
            public double speed;
            public float mach;
            public double dragAccelMagnitude;
        }

        // Back out a constant "how hard does drag pull per unit density per unit speed-squared"
        // factor from the vessel's CURRENT real drag acceleration, instead of computing it from
        // parts. Pass in the vessel's current measured drag acceleration magnitude (currently
        // AccelerationSurfaceSmoothed minus graviticAcceleration, pure drag as long as the engines
        // are off) along with the density and airspeed the game is experiencing right now at the
        // vessel's current position. Returns 0 (safe, means "predict as if no drag") if speed or
        // density are too small to calibrate against.
        //
        // Density floor of 1e-4 kg/m3: below that the air is thin enough that density sits near
        // the noise floor of whatever AccelerationSurfaceSmoothed is reading, and dividing a noisy
        // numerator by a near-zero denominator amplifies that noise into a wildly wrong factor.
        // Real drag is negligible for this vehicle below that density anyway.
        public static double CalibrateBallisticFactor(double currentDragAccel, double currentDensity, double currentSpeed)
        {
            if (currentDensity < 1e-4 || currentSpeed < 1.0)
                return 0;

            return currentDragAccel / (currentDensity * currentSpeed * currentSpeed);
        }

        // ballisticFactor: from CalibrateBallisticFactor above - pass 0 to predict as pure vacuum
        // (a sanity check against compute_real_collision's own answer on the same trajectory
        // before trusting this on an atmospheric one).
        // timeStep/maxSteps: keep the product (total simulated time covered) comfortably above
        // how long the real descent should take, but see the performance note on the class - this
        // is not free.
        public static Result PredictImpact(
            IKeplerPatch orbit,
            CelestialBodyComponent body,
            double startUT,
            double ballisticFactor,
            double timeStep = 1.0,
            int maxSteps = 2000)
        {
            Vector3d r0 = orbit.GetRelativePositionAtUTZup(startUT);
            Vector3d v0 = orbit.GetOrbitalVelocityAtUTZup(startUT);
            return PredictImpact(r0, v0, body, startUT, ballisticFactor, timeStep, maxSteps);
        }

        // Same as above, but from an explicit (r0, v0) state vector instead of a live orbit - for
        // evaluating a HYPOTHETICAL trajectory (a burn that hasn't happened, or wouldn't happen
        // until some future candidate time) rather than the vessel's actual current orbit.
        // LandingTargeting.cs's deorbit-burn search needs exactly this: it scores dozens of
        // candidate burns per search, none of which are real orbits to hand in as an IKeplerPatch.
        // The orbit-based overload above is just this with r0/v0 read off the orbit at startUT.
        public static Result PredictImpact(
            Vector3d r0,
            Vector3d v0,
            CelestialBodyComponent body,
            double startUT,
            double ballisticFactor,
            double timeStep = 1.0,
            int maxSteps = 2000)
        {
            var result = new Result { found = false };

            double mu = body.gravParameter;
            double bodyRadius = body.radius;
            double atmosphereTopRadius = bodyRadius + body.atmosphereDepth;

            Vector3d r = r0;
            Vector3d v = v0;

            double ut = startUT;

            // Fast-forward: while still above the atmosphere there's zero drag in this model (see
            // the "radius < atmosphereTopRadius" gate inside the loop below), so stepping through
            // that part one timeStep at a time is pure waste - it's exactly the same answer
            // KeplerPropagator gives in one call. Jump straight to the moment the trajectory
            // crosses atmosphereTopRadius, then hand off to the normal per-step loop. Search out to
            // the same total time budget the per-step loop would have covered (maxSteps * timeStep)
            // so a trajectory that never comes back down within that window still correctly
            // reports "not found" rather than searching forever.
            if (r.magnitude > atmosphereTopRadius)
            {
                double totalBudget = maxSteps * timeStep;
                double coarseStep = Math.Max(timeStep * 10, 1.0);
                double tSearched = 0;
                bool crossed = false;

                while (tSearched < totalBudget)
                {
                    double tTry = Math.Min(tSearched + coarseStep, totalBudget);
                    KeplerPropagator.Propagate(r0, v0, mu, tTry, out Vector3d rTry, out _);

                    if (rTry.magnitude <= atmosphereTopRadius)
                    {
                        double loT = tSearched, hiT = tTry;
                        for (int i = 0; i < 40; i++)
                        {
                            double midT = 0.5 * (loT + hiT);
                            KeplerPropagator.Propagate(r0, v0, mu, midT, out Vector3d rMid, out _);
                            if (rMid.magnitude > atmosphereTopRadius) loT = midT; else hiT = midT;
                        }

                        KeplerPropagator.Propagate(r0, v0, mu, hiT, out r, out v);
                        ut = startUT + hiT;
                        crossed = true;
                        break;
                    }

                    if (tTry >= totalBudget)
                        break;

                    tSearched = tTry;
                }

                // Never dips into the atmosphere within the usual time budget (periapsis stays
                // above atmosphereTopRadius, or the vessel's headed the wrong way).
                if (!crossed)
                    return result;
            }

            for (int step = 0; step < maxSteps; step++)
            {
                double radius = r.magnitude;
                double altitude = radius - bodyRadius;

                // Real terrain check (same frame/axis-swap trick as compute_real_collision) once
                // low enough that it's worth the cost every step - above that just watch the
                // simple spherical altitude, no need to sample real terrain from orbital height.
                if (altitude < 20000)
                {
                    Vector3d rel_pos = new Vector3d(r.x, r.z, r.y); // Zup -> Yup
                    Position ps = new Position(body.SimulationObject.transform.celestialFrame, rel_pos);
                    body.GetAltitudeFromTerrain(ps, out double terrainAltitude, out _);

                    if (terrainAltitude < 0)
                    {
                        result.found = true;
                        result.impactUT = ut;
                        result.impactSpeed = v.magnitude;
                        result.stepsTaken = step;

                        body.GetLatLonAltFromRadius(ps, out result.impactLat, out result.impactLon, out _);
                        return result;
                    }
                }
                else if (altitude < 0)
                {
                    // Fallback in case the spherical altitude went negative before the 20km terrain
                    // check window kicked in (steep/fast trajectory skipping a step past it) -
                    // still resolve a real terrain-relative answer rather than reporting nothing.
                    Vector3d rel_pos = new Vector3d(r.x, r.z, r.y);
                    Position ps = new Position(body.SimulationObject.transform.celestialFrame, rel_pos);
                    result.found = true;
                    result.impactUT = ut;
                    result.impactSpeed = v.magnitude;
                    result.stepsTaken = step;
                    body.GetLatLonAltFromRadius(ps, out result.impactLat, out result.impactLon, out _);
                    return result;
                }

                Vector3d gravityAccel = r * (-mu / (radius * radius * radius));

                Vector3d totalAccel = gravityAccel;
                if (radius < atmosphereTopRadius && ballisticFactor > 0)
                {
                    double density = body.GetDensity(body.GetPressure(altitude), body.GetTemperature(altitude));
                    if (density > 0)
                    {
                        double speed = v.magnitude;
                        // NOTE: this is velocity relative to the body's INERTIAL frame, not the
                        // (rotating) atmosphere - see simplification #1 in the class comment.
                        Vector3d dragAccel = v * (-ballisticFactor * density * speed);
                        totalAccel += dragAccel;
                    }
                }

                // Semi-implicit (symplectic) Euler - update velocity first, then use the NEW
                // velocity to step position. Cheap and noticeably more stable over many steps than
                // plain forward Euler, though still first-order (see simplification #3).
                v += totalAccel * timeStep;
                r += v * timeStep;
                ut += timeStep;
            }

            return result; // found stays false - didn't hit anything within maxSteps
        }

        // Replaces the CONSTANT ballisticFactor above with a real, per-step drag computation for
        // burn planning specifically, so the search doesn't need a real prior descent
        // (LandingPilot.calibrated_k_retrograde_by_vessel) to have anything to work with -
        // mach and pseudoReynolds (density*speed) are recomputed fresh every step from the
        // vessel's real local state, and totalAreaDrag is reinterpolated from dragProfile at that
        // step's mach.
        //
        // Simplification #1 (atmosphere corotation ignored) still applies here, deliberately -
        // dragProfile-based drag is computed against the same inertial-frame velocity `v` the
        // ballisticFactor version uses. Semi-implicit Euler (simplification #3) and the vacuum
        // fast-forward (simplification #4, exact) both carry over unmodified - only the per-step
        // drag SOURCE differs from the method above.
        //
        // dragProfile: from VesselAeroLookup.BuildFixedAttitudeDragProfile - a table, not a live
        // reading, so this works whether the vessel has ever flown a real atmospheric descent or
        // not. dragProfile.found == false (vacuum body, or a profile build that failed) behaves
        // exactly like ballisticFactor == 0 above: pure vacuum/Kepler prediction, no drag term.
        // vesselMassTonnes: VesselComponent.totalMass - same kN-force/tonnes-mass convention used
        // everywhere else in this codebase.
        public static Result PredictImpact(
            IKeplerPatch orbit,
            CelestialBodyComponent body,
            double startUT,
            VesselAeroLookup.AeroDragProfile dragProfile,
            double vesselMassTonnes,
            double timeStep = 1.0,
            int maxSteps = 2000)
        {
            Vector3d r0 = orbit.GetRelativePositionAtUTZup(startUT);
            Vector3d v0 = orbit.GetOrbitalVelocityAtUTZup(startUT);
            return PredictImpact(r0, v0, body, startUT, dragProfile, vesselMassTonnes, timeStep, maxSteps);
        }

        // Same as above, but from an explicit (r0, v0) state vector - the overload
        // FindBestDeorbitBurn's search actually needs, same reasoning as the ballisticFactor
        // version's own (r0, v0) overload above.
        //
        // captureTrace/traceEveryNSteps: see Result.trace's own comment - off by default (every
        // one of the 36-per-search candidate evaluations should stay exactly as cheap as before).
        // DeorbitBurn.cs turns this on for a single extra re-run of the winning candidate only,
        // purely to get diagnostic waypoints logged - never inside the search loop itself.
        public static Result PredictImpact(
            Vector3d r0,
            Vector3d v0,
            CelestialBodyComponent body,
            double startUT,
            VesselAeroLookup.AeroDragProfile dragProfile,
            double vesselMassTonnes,
            double timeStep = 1.0,
            int maxSteps = 2000,
            bool captureTrace = false,
            int traceEveryNSteps = 10)
        {
            var result = new Result { found = false, trace = captureTrace ? new List<Waypoint>() : null };

            double mu = body.gravParameter;
            double bodyRadius = body.radius;
            double atmosphereTopRadius = bodyRadius + body.atmosphereDepth;

            Vector3d r = r0;
            Vector3d v = v0;

            double ut = startUT;

            // Identical vacuum fast-forward to the ballisticFactor version above - exact
            // optimization for the same reason: no drag above atmosphereTopRadius in this model.
            if (r.magnitude > atmosphereTopRadius)
            {
                double totalBudget = maxSteps * timeStep;
                double coarseStep = Math.Max(timeStep * 10, 1.0);
                double tSearched = 0;
                bool crossed = false;

                while (tSearched < totalBudget)
                {
                    double tTry = Math.Min(tSearched + coarseStep, totalBudget);
                    KeplerPropagator.Propagate(r0, v0, mu, tTry, out Vector3d rTry, out _);

                    if (rTry.magnitude <= atmosphereTopRadius)
                    {
                        double loT = tSearched, hiT = tTry;
                        for (int i = 0; i < 40; i++)
                        {
                            double midT = 0.5 * (loT + hiT);
                            KeplerPropagator.Propagate(r0, v0, mu, midT, out Vector3d rMid, out _);
                            if (rMid.magnitude > atmosphereTopRadius) loT = midT; else hiT = midT;
                        }

                        KeplerPropagator.Propagate(r0, v0, mu, hiT, out r, out v);
                        ut = startUT + hiT;
                        crossed = true;
                        break;
                    }

                    if (tTry >= totalBudget)
                        break;

                    tSearched = tTry;
                }

                if (!crossed)
                    return result;
            }

            bool haveDragSource = dragProfile.found && vesselMassTonnes > 0.001;

            for (int step = 0; step < maxSteps; step++)
            {
                double radius = r.magnitude;
                double altitude = radius - bodyRadius;

                // Identical terrain check to the ballisticFactor version above.
                if (altitude < 20000)
                {
                    Vector3d rel_pos = new Vector3d(r.x, r.z, r.y); // Zup -> Yup
                    Position ps = new Position(body.SimulationObject.transform.celestialFrame, rel_pos);
                    body.GetAltitudeFromTerrain(ps, out double terrainAltitude, out _);

                    if (terrainAltitude < 0)
                    {
                        result.found = true;
                        result.impactUT = ut;
                        result.impactSpeed = v.magnitude;
                        result.stepsTaken = step;

                        body.GetLatLonAltFromRadius(ps, out result.impactLat, out result.impactLon, out _);
                        return result;
                    }
                }
                else if (altitude < 0)
                {
                    Vector3d rel_pos = new Vector3d(r.x, r.z, r.y);
                    Position ps = new Position(body.SimulationObject.transform.celestialFrame, rel_pos);
                    result.found = true;
                    result.impactUT = ut;
                    result.impactSpeed = v.magnitude;
                    result.stepsTaken = step;
                    body.GetLatLonAltFromRadius(ps, out result.impactLat, out result.impactLon, out _);
                    return result;
                }

                Vector3d gravityAccel = r * (-mu / (radius * radius * radius));

                Vector3d totalAccel = gravityAccel;

                // Only meaningful when haveDragSource actually ran the block below for this step -
                // stay at 0 otherwise (still-in-vacuum-ish steps, or density/speed too low to
                // matter), so a trace waypoint always has a well-defined value to report rather
                // than whatever was left over from a previous step.
                float traceMach = 0f;
                double traceDragAccelMagnitude = 0;

                if (radius < atmosphereTopRadius && haveDragSource)
                {
                    double pressureKPa = body.GetPressure(altitude);
                    double temperature = body.GetTemperature(altitude);
                    double density = body.GetDensity(pressureKPa, temperature);

                    if (density > 0)
                    {
                        double speed = v.magnitude;
                        if (speed > 0.1)
                        {
                            // Same formula as VesselAeroLookup.EvaluateVesselAeroPureRetrogradeAtState
                            // - see that method's own comment. NOTE: speed here is inertial-frame,
                            // same known gap as the ballisticFactor version above (simplification #1).
                            double soundSpeed = AeroUtilities.GetSoundSpeed(body.atmosphereAdiabaticIndex, pressureKPa, density);
                            float mach = soundSpeed > 0 ? (float)(speed / soundSpeed) : 0f;

                            double dynamicPressureKPa = 0.0005 * density * speed * speed;
                            double dynamicPressurePa = dynamicPressureKPa * 1000.0;
                            double pseudoReynolds = density * speed;
                            float pseudoReynoldsFactor = PhysicsSettings.DragCurvePseudoReynolds.Evaluate((float)pseudoReynolds);

                            // Constant assumed angle, not a tracked one - see this class's field
                            // comment on AssumedAngleOffRetrogradeDeg. Nothing actively holds the
                            // vessel's attitude relative to any reference during most of the coast
                            // (it's on rails under time warp), so there's no "angle since some
                            // anchor" to track - the vessel instead settles into a passive
                            // aerodynamic equilibrium once deep enough in real air to matter.
                            double totalAreaDrag = VesselAeroLookup.SampleDragProfile(dragProfile, mach, AssumedAngleOffRetrogradeDeg);

                            // Same dragScalar formula as Module_Drag.UpdateDrag/EvaluateVesselAeroCore
                            // - see VesselAeroLookup.cs's class comment - just with totalAreaDrag
                            // coming from the interpolated table instead of a fresh per-part
                            // GetAeroDataForDirection call every step.
                            double totalForceEstimateKN = totalAreaDrag
                                * PhysicsSettings.DragCubeMultiplier
                                * pseudoReynoldsFactor
                                * PhysicsSettings.DragMultiplier
                                * dynamicPressurePa
                                * 0.001;

                            // kN / tonnes = m/s^2 directly, same convention BurndV.cs relies on -
                            // no extra conversion needed.
                            double dragAccelMagnitude = totalForceEstimateKN / vesselMassTonnes;
                            Vector3d dragAccel = v * (-dragAccelMagnitude / speed);
                            totalAccel += dragAccel;

                            traceMach = mach;
                            traceDragAccelMagnitude = dragAccelMagnitude;
                        }
                    }
                }

                if (captureTrace && step % traceEveryNSteps == 0)
                {
                    result.trace.Add(new Waypoint
                    {
                        ut = ut,
                        altitude = altitude,
                        speed = v.magnitude,
                        mach = traceMach,
                        dragAccelMagnitude = traceDragAccelMagnitude,
                    });
                }

                v += totalAccel * timeStep;
                r += v * timeStep;
                ut += timeStep;
            }

            return result; // found stays false - didn't hit anything within maxSteps
        }
    }
}
