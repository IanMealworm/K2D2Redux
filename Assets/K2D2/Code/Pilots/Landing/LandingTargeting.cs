using System;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing
{
    // Precision landing's deorbit math. See the comment block above FindBestDeorbitBurn for the
    // current approach. DeorbitBurn/MidCourseCorrection aim straight at the true target, and
    // TouchDown's own closed-loop steering (see TouchDown.cs) handles the descent from Brake
    // onward.
    public static class LandingTargeting
    {
        // ------------------------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------------------------

        // Raw longitude (degrees) of a body-relative Zup position vector, using the same frame
        // math as LandingPilot.compute_real_collision() (Zup->Yup swap, Position built against
        // body.SimulationObject.transform.celestialFrame). "Raw" here means: not yet corrected for
        // how much the body will have additionally rotated between now and whatever future moment
        // this position corresponds to - see WrapDegrees180/the rotation-compensation note on
        // PredictImpactLongitude below for why that correction matters once the time horizon is
        // more than a couple of minutes.
        static double RawLongitudeOfPosition(CelestialBodyComponent body, Vector3d rel_pos_zup)
        {
            Vector3d rel_pos_yup = new Vector3d(rel_pos_zup.x, rel_pos_zup.z, rel_pos_zup.y);
            Position pos = new Position(body.SimulationObject.transform.celestialFrame, rel_pos_yup);
            body.GetLatLonAltFromRadius(pos, out _, out double lon, out _);
            return lon;
        }

        // Longitude (degrees) of the vessel's position at UT, using the same Zup->Yup swap and
        // body-relative Position construction as compute_real_collision(). "Raw" - see above.
        public static double GetLongitudeAtUT(IKeplerPatch orbit, CelestialBodyComponent body, double UT)
        {
            return RawLongitudeOfPosition(body, orbit.GetRelativePositionAtUTZup(UT));
        }

        static double WrapDegrees180(double deg)
        {
            deg %= 360.0;
            if (deg > 180.0) deg -= 360.0;
            if (deg < -180.0) deg += 360.0;
            return deg;
        }

        // Current orbital period from a state vector pair (vis-viva -> semi-major axis ->
        // Kepler's third law). Used to size the deorbit-timing search window below to "about one
        // of our own orbits" regardless of what body/altitude we're at.
        public static double OrbitalPeriodFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            double sma = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);
            return 2.0 * Math.PI * Math.Sqrt(sma * sma * sma / gravParameter);
        }

        // For Circularize.cs (precision landing's precondition phase - circularize before
        // deorbiting, rather than teaching the deorbit search to handle eccentric starting
        // orbits). Semi-major axis from vis-viva (same formula as OrbitalPeriodFromStateVectors
        // above), apoapsis/periapsis radii from the standard eccentricity vector
        // (e = ((v^2 - mu/r) r - (r.v) v) / mu) rather than anything engine-specific - deliberately
        // avoids IKeplerPatch/PatchedConicsOrbit member access entirely (unlike ManeuverCreator's
        // existing Circularize* methods, which read orbit.Apoapsis/orbit.Periapsis off a
        // "GetLastOrbit() as PatchedConicsOrbit" cast - the same cast pattern that throws
        // InvalidCastException for the actively-flown vessel under Redux, since its orbit is a
        // CurrentPatchedConicsOrbit, not a PatchedConicsOrbit).
        public static void OrbitalElementsFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter,
            out double semiMajorAxis, out double apoapsisRadius, out double periapsisRadius)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            semiMajorAxis = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);

            double rDotV = Vector3d.Dot(r0, v0);
            Vector3d eccVec = ((v0mag * v0mag - gravParameter / r0mag) * r0 - rDotV * v0) / gravParameter;
            double eccentricity = eccVec.magnitude;

            apoapsisRadius = semiMajorAxis * (1.0 + eccentricity);
            periapsisRadius = semiMajorAxis * (1.0 - eccentricity);
        }

        // Not a real orbital period - a hyperbolic/unbound trajectory only ever passes periapsis
        // once, it doesn't repeat. This is just a sensible time SCALE to sweep across when
        // looking for that one passage (see TimeToNextPeriapsis's caller in NodeExPilot.
        // CreateCircularizeNode - the "Circularize at PE" button needs to work on an inbound SOI
        // capture trajectory, before any capture burn has happened). Same formula as
        // OrbitalPeriodFromStateVectors above, but taking the absolute value of the semi-major
        // axis (negative for a hyperbolic/unbound orbit - see OrbitalElementsFromStateVectors) so
        // the result stays finite and positive instead of NaN, plus a 2x safety margin since -
        // unlike a real period - there's no guarantee this "characteristic timescale" is itself
        // long enough to contain the passage being searched for.
        public static double SearchWindowFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            double sma = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);
            return 4.0 * Math.PI * Math.Sqrt(Math.Abs(sma * sma * sma) / gravParameter);
        }

        // Time (seconds from now) until the current orbit's next apoapsis passage - needed to
        // time the Circularize burn (standard practice: circularize by raising periapsis at
        // apoapsis). Found by propagating forward with KeplerPropagator and bisecting on the sign
        // of radial velocity (r.v: positive while outbound/before apoapsis, negative once inbound/
        // past it) rather than deriving it from a closed-form Kepler's-equation true/eccentric/
        // mean-anomaly chain - more propagator calls, but far fewer places for a quadrant/sign
        // mistake to hide.
        public static double TimeToNextApoapsis(Vector3d r0, Vector3d v0, double gravParameter, double period)
        {
            const int samples = 24;
            double stepSize = period / samples;

            double prevDt = 0;
            double prevRadialVelocity = Vector3d.Dot(r0, v0);

            for (int i = 1; i <= samples; i++)
            {
                double dt = i * stepSize;
                KeplerPropagator.Propagate(r0, v0, gravParameter, dt, out Vector3d r, out Vector3d v);
                double radialVelocity = Vector3d.Dot(r, v);

                if (prevRadialVelocity > 0 && radialVelocity <= 0)
                {
                    double lo = prevDt, hi = dt;
                    for (int j = 0; j < 40; j++)
                    {
                        double mid = (lo + hi) / 2.0;
                        KeplerPropagator.Propagate(r0, v0, gravParameter, mid, out Vector3d rm, out Vector3d vm);
                        if (Vector3d.Dot(rm, vm) > 0) lo = mid; else hi = mid;
                    }
                    return (lo + hi) / 2.0;
                }

                prevDt = dt;
                prevRadialVelocity = radialVelocity;
            }

            // Radial velocity was already <= 0 from the very first sample - we're at/essentially
            // past apoapsis this instant. "Burn now" beats leaving a caller with a stale time.
            return 0;
        }

        // Mirror of TimeToNextApoapsis above, for the Node tab's "Circularize at PE" button -
        // same propagate-and-bisect approach, watching for the opposite sign flip (radial velocity
        // negative/inbound -> positive/outbound, i.e. periapsis passage).
        public static double TimeToNextPeriapsis(Vector3d r0, Vector3d v0, double gravParameter, double period)
        {
            const int samples = 24;
            double window = period;

            // Retries with a doubled window if no crossing turns up - needed for the
            // hyperbolic/unbound case (Circularize at PE during an SOI capture), where "period" is
            // really SearchWindowFromStateVectors's heuristic estimate rather than a guaranteed-
            // correct orbital period, so a single pass "not found" might just mean the window
            // undershot. A bound (elliptical) caller's real period always contains one, so this
            // loop is a no-op for that case - purely extra insurance for the hyperbolic one.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                double stepSize = window / samples;

                double prevDt = 0;
                double prevRadialVelocity = Vector3d.Dot(r0, v0);

                for (int i = 1; i <= samples; i++)
                {
                    double dt = i * stepSize;
                    KeplerPropagator.Propagate(r0, v0, gravParameter, dt, out Vector3d r, out Vector3d v);
                    double radialVelocity = Vector3d.Dot(r, v);

                    if (prevRadialVelocity < 0 && radialVelocity >= 0)
                    {
                        double lo = prevDt, hi = dt;
                        for (int j = 0; j < 40; j++)
                        {
                            double mid = (lo + hi) / 2.0;
                            KeplerPropagator.Propagate(r0, v0, gravParameter, mid, out Vector3d rm, out Vector3d vm);
                            if (Vector3d.Dot(rm, vm) < 0) lo = mid; else hi = mid;
                        }
                        return (lo + hi) / 2.0;
                    }

                    prevDt = dt;
                    prevRadialVelocity = radialVelocity;
                }

                window *= 2.0;
            }

            // Radial velocity was already >= 0 from the very first sample - we're at/essentially
            // past periapsis this instant. Same reasoning as TimeToNextApoapsis's own fallback.
            return 0;
        }

        // ------------------------------------------------------------------------------------
        // Deorbit-timing search
        // ------------------------------------------------------------------------------------
        //
        // From a stable parking orbit, precision landing needs TWO things at once: an actual
        // deorbit (periapsis dropped below terrain) AND an impact point that lands on the
        // target's longitude. This searches continuously over WHEN, within roughly the next one of
        // our own orbits, to perform a normal-sized deorbit burn (retrograde, dropping periapsis
        // to a safe-below-terrain altitude - same vis-viva idea as the existing
        // ManeuverCreator.ChangePeriapsis, phrased in raw state vectors instead of touching the
        // live orbit's own Apoapsis/Periapsis properties). Because the search sweeps continuously
        // through a full 360 degrees of "where we are when we burn," the resulting impact point
        // sweeps past the target longitude exactly once per orbit (up to the sampling resolution
        // below), and the search just has to find it. KeplerPropagator.cs does the "where would a
        // hypothetical burn end up" math without touching the live vessel's orbit.
        //
        // Assumes a roughly circular starting orbit (treats the burn point as both apoapsis and
        // periapsis of the current orbit when sizing the deorbit burn) - true for the normal
        // "park in a circular orbit, then land" flow this is built for. A genuinely eccentric
        // starting orbit would need a proper eccentricity-vector-based version of
        // ComputeDeorbitDeltaV instead.

        // Vis-viva retrograde delta-v to drop periapsis to targetPeriapsisRadius, burning at a
        // point of radius r0mag / speed v0mag (treated as the current near-circular orbit's
        // apoapsis-equivalent, per the note above).
        public static double ComputeDeorbitDeltaV(double r0mag, double v0mag, double gravParameter, double targetPeriapsisRadius)
        {
            double newSMA = (r0mag + targetPeriapsisRadius) / 2.0;
            double newSpeed = VisVivaEquation.CalculateVelocityFromSMA(r0mag, newSMA, gravParameter);
            return newSpeed - v0mag;
        }

        // Reprojects a Zup-frame body-relative position vector that's only valid AT futureUT (a
        // hypothetical future/past instant) into an equivalent vector expressed as if it were
        // RIGHT NOW instead - i.e. the same physical point on the rotating body, re-described
        // through the body's current orientation. UniverseModel.ZupAtUT(UT, body, ref frame)
        // gives the body's real CelestialFrame at any UT; WorldToLocal at the future frame
        // followed by LocalToWorld at "now"'s frame does the reprojection using nothing but that
        // struct's own math. This lets the existing Position/celestialFrame("now")-based terrain
        // and lat/lon calls below keep working unchanged - they just receive an input vector
        // that's already been corrected for rotation.
        static Vector3d ReprojectToNow(CelestialBodyComponent body, double futureUT, Vector3d rel_pos_zup_at_futureUT)
        {
            CelestialFrame frameAtFuture = default;
            GeneralTools.Game.UniverseModel.ZupAtUT(futureUT, body, ref frameAtFuture);

            CelestialFrame frameNow = default;
            GeneralTools.Game.UniverseModel.ZupAtUT(GeneralTools.Current_UT, body, ref frameNow);

            Vector3d localBodyFixed = frameAtFuture.WorldToLocal(rel_pos_zup_at_futureUT);
            return frameNow.LocalToWorld(localBodyFixed);
        }

        // Propagates (r0, v0) forward with our own standalone Kepler propagator (not the live
        // orbit - this lets us evaluate a hypothetical burn without performing it) until it
        // crosses the terrain, using the same bisection search compute_real_collision() uses.
        // Returns the impact lat/lon.
        //
        // Rotation compensation: the body keeps rotating during the coast to impact, so the raw
        // longitude at impactUT has to be corrected by how far the body has turned since now. The
        // rotation rate is scaled by an empirical calibration factor rather than applied at face
        // value - see empirical_calibration below.
        public static bool PredictImpactLongitude(Vector3d r0, Vector3d v0, CelestialBodyComponent body, double startUT,
            out double lon, out double lat, out double impactUT)
        {
            double mu = body.gravParameter;
            double t = 30; // small offset past the burn itself, same idea as compute_real_collision()'s 2-minute head start
            double deltaTime = 60;
            int maxOccurrences = 150;
            double terrainAltitude = 0;
            bool collide = false;

            for (int i = 0; i < maxOccurrences; i++)
            {
                KeplerPropagator.Propagate(r0, v0, mu, t, out Vector3d r_zup, out _);
                Vector3d r_yup = new Vector3d(r_zup.x, r_zup.z, r_zup.y);
                Position ps = new Position(body.SimulationObject.transform.celestialFrame, r_yup);
                body.GetAltitudeFromTerrain(ps, out terrainAltitude, out double sceneryOffset);

                if (terrainAltitude < 0)
                {
                    collide = true;
                    if (deltaTime > 0) deltaTime = -deltaTime / 2;
                    t += deltaTime;
                }
                else
                {
                    if (deltaTime < 0) deltaTime = -deltaTime / 2;
                    t += deltaTime;
                }

                if (Math.Abs(terrainAltitude) < 1)
                    break;
            }

            impactUT = startUT + t;

            KeplerPropagator.Propagate(r0, v0, mu, t, out Vector3d final_zup, out _);
            double raw_lon = RawLongitudeOfPosition(body, final_zup);
            Vector3d final_yup = new Vector3d(final_zup.x, final_zup.z, final_zup.y);
            Position finalPos = new Position(body.SimulationObject.transform.celestialFrame, final_yup);
            body.GetLatLonAltFromRadius(finalPos, out lat, out _, out _);

            // Empirical calibration on top of the naive linear rotation-rate correction - keeps
            // real accuracy in the tens-of-km ballpark. See ReprojectToNow above for the
            // alternative (ZupAtUT-based) approach kept in reserve if this needs revisiting.
            const double empirical_calibration = 1.307;
            double rotation_deg = (impactUT - GeneralTools.Current_UT) * 360.0 / body.rotationPeriod * empirical_calibration;
            lon = WrapDegrees180(raw_lon - rotation_deg);

            return collide;
        }

        // Same bisection-search-for-a-terrain-altitude-crossing as PredictImpactLongitude above,
        // targeting an arbitrary altitude threshold instead of exactly 0 - used by
        // LandingPilot.compute_startBurn() to find the REAL UT the current trajectory crosses
        // start_touchdown_altitude + min_correction_altitude_margin, instead of estimating it from
        // a speed division. A speed-based estimate has to pick between two bad options: divide by
        // the full orbital speed (safe, but underestimates real descent time, since altitude only
        // drops via the much smaller vertical component of that speed) or divide by vertical speed
        // at the eventual impact point (blows up near periapsis, where vertical speed is ~0 by
        // definition). Directly searching the propagated trajectory for the real crossing avoids
        // both failure modes.
        public static bool PredictUTAtAltitude(Vector3d r0, Vector3d v0, CelestialBodyComponent body, double startUT,
            double targetAltitudeM, out double crossingUT)
        {
            double mu = body.gravParameter;
            double t = 30; // same small head-start as PredictImpactLongitude above
            double deltaTime = 60;
            int maxOccurrences = 150;
            double terrainAltitude = 0;
            bool crossed = false;

            for (int i = 0; i < maxOccurrences; i++)
            {
                KeplerPropagator.Propagate(r0, v0, mu, t, out Vector3d r_zup, out _);
                Vector3d r_yup = new Vector3d(r_zup.x, r_zup.z, r_zup.y);
                Position ps = new Position(body.SimulationObject.transform.celestialFrame, r_yup);
                body.GetAltitudeFromTerrain(ps, out terrainAltitude, out double sceneryOffset);

                if (terrainAltitude < targetAltitudeM)
                {
                    crossed = true;
                    if (deltaTime > 0) deltaTime = -deltaTime / 2;
                    t += deltaTime;
                }
                else
                {
                    if (deltaTime < 0) deltaTime = -deltaTime / 2;
                    t += deltaTime;
                }

                if (Math.Abs(terrainAltitude - targetAltitudeM) < 1)
                    break;
            }

            crossingUT = startUT + t;
            return crossed;
        }

        // The actual search: samples candidate burn times across roughly one orbit, evaluates the
        // resulting impact point for each (via ComputeDeorbitDeltaV + PredictImpactLongitude
        // above) against BOTH target latitude and longitude, and refines around whichever sample
        // came out best for a tighter final answer.
        //
        // Worth being honest about what this can and can't do: one burn from one fixed-inclination
        // orbit is really one degree of freedom (when in the orbit to burn), which determines both
        // the resulting latitude and longitude together, not independently - so this searches for
        // the single best-achievable point across the orbit, not a guaranteed exact hit on an
        // arbitrary target. If the target's latitude is outside the orbit's inclination band, no
        // burn timing reaches it at all; that's a real plane mismatch the player has to fix, not
        // something this search (or the descent-phase steering) can paper over.
        //
        // Returns false if no candidate produced a terrain impact at all (shouldn't normally
        // happen given targetPeriapsisRadius is meant to be safely below terrain, but an unusual
        // body/orbit combination could still miss).
        // bestLon/bestLat/bestErrorM/bestImpactUT are the search's own prediction for whichever
        // candidate it picked - exposed so the caller can log them for comparison against the real
        // in-game landing result.
        // maxPlaneTrimDv (m/s, player-tunable via the Max Plane Trim slider - 0 disables this
        // entirely) is a SMALL normal/antinormal component added on top of the usual
        // prograde/retrograde deorbit burn - see the plane-trim block at the end of this method.
        // Time-distance penalty for the search below: without it, a candidate burn time that's
        // numerically a hair better on raw ground-miss distance but sits almost a full orbit away
        // can win outright over a much nearer candidate, even though the "improvement" is usually
        // within the search's own noise floor. Multiplying elapsed wait time by this constant and
        // adding it ONLY to the score used to pick a winner (bestScoredErrorM below - never to
        // bestErrorM/errors[], which stay the true predicted miss distance for logging, the
        // in-game readout, and the parabola refinement's own vertex fit) pushes ties and near-ties
        // toward the nearest usable candidate instead.
        const double distance_penalty_per_second = 0.2;

        public static bool FindBestDeorbitBurn(IKeplerPatch orbit, CelestialBodyComponent body,
            double searchStartUT, double searchDuration, double targetPeriapsisRadius,
            double targetLatitudeDeg, double targetLongitudeDeg, double maxPlaneTrimDv,
            out double bestUT, out double bestDeltaV, out double bestNormalDeltaV,
            out double bestLon, out double bestLat, out double bestErrorM,
            out double bestImpactUT)
        {
            const int samples = 36;

            bestUT = searchStartUT;
            bestDeltaV = 0;
            bestNormalDeltaV = 0;
            bestLon = 0;
            bestLat = 0;
            bestErrorM = double.MaxValue;
            bestImpactUT = 0;
            int bestIndex = -1;
            // Tracks the distance-penalized score used for every comparison below - kept
            // separate from bestErrorM (the true, unpenalized miss distance) so the penalty never
            // leaks into anything reported to the player or fed into the parabola fits.
            double bestScoredErrorM = double.MaxValue;

            // Only the per-sample error needs to survive the whole loop (to look back at the
            // winning sample's neighbors for refinement below) - everything else about the
            // winning candidate is already captured into best* as soon as it's found.
            double[] errors = new double[samples];
            bool[] valid = new bool[samples];

            for (int i = 0; i < samples; i++)
            {
                double candidateUT = searchStartUT + searchDuration * i / (samples - 1);

                if (TryEvaluateCandidate(orbit, body, candidateUT, targetPeriapsisRadius,
                        targetLatitudeDeg, targetLongitudeDeg, 0,
                        out double dv, out double lon, out double lat, out double errorM, out double impactUT))
                {
                    valid[i] = true;
                    errors[i] = errorM;

                    double scoredErrorM = errorM + distance_penalty_per_second * (candidateUT - searchStartUT);
                    if (scoredErrorM < bestScoredErrorM)
                    {
                        bestScoredErrorM = scoredErrorM;
                        bestErrorM = errorM;
                        bestIndex = i;
                        bestUT = candidateUT;
                        bestDeltaV = dv;
                        bestLon = lon;
                        bestLat = lat;
                        bestImpactUT = impactUT;
                    }
                }
            }

            if (bestIndex < 0)
                return false;

            // Refine around the winning sample by fitting a parabola through the winner and its
            // two neighbors and jumping to that parabola's vertex - standard 1D local-minimum
            // refinement (an unsigned lat/lon distance never crosses zero, it just dips toward a
            // minimum, so a signed zero-crossing search doesn't apply here), and cheap (one extra
            // candidate evaluation).
            if (bestIndex > 0 && bestIndex < samples - 1 && valid[bestIndex - 1] && valid[bestIndex + 1])
            {
                double h = searchDuration / (samples - 1);
                double y0 = errors[bestIndex - 1];
                double y1 = errors[bestIndex];
                double y2 = errors[bestIndex + 1];
                double denom = y0 - 2 * y1 + y2;

                // denom > 0 means the three samples genuinely curve upward around the winner (a
                // real local minimum, not noise or a flat run) - only trust the parabola then.
                if (denom > 1e-9)
                {
                    double offset = 0.5 * (y0 - y2) / denom;
                    // A parabola fit to three samples can still suggest stepping outside their
                    // own span on a shallow or asymmetric error curve - clamp to the sample
                    // spacing itself so this can only interpolate between the three points it was
                    // fit to, never extrapolate past them.
                    offset = Math.Clamp(offset, -1.0, 1.0);
                    double refinedUT = bestUT + offset * h;

                    if (TryEvaluateCandidate(orbit, body, refinedUT, targetPeriapsisRadius,
                            targetLatitudeDeg, targetLongitudeDeg, 0,
                            out double dvR, out double lonR, out double latR, out double errorR, out double impactUTR))
                    {
                        double scoredErrorR = errorR + distance_penalty_per_second * (refinedUT - searchStartUT);
                        if (scoredErrorR < bestScoredErrorM)
                        {
                            bestScoredErrorM = scoredErrorR;
                            bestErrorM = errorR;
                            bestUT = refinedUT;
                            bestDeltaV = dvR;
                            bestLon = lonR;
                            bestLat = latR;
                            bestImpactUT = impactUTR;
                        }
                    }
                }
            }

            // Optional small plane trim, on top of the timing search above. A normal/antinormal
            // component added to the burn nudges the orbital PLANE itself (unlike the
            // prograde/retrograde component, which can only move you along the plane you're
            // already on) - so unlike the timing search, this gives the search a second degree of
            // freedom, and could in principle drive the error arbitrarily close to zero with
            // enough of it. Deliberately kept small and player-capped (maxPlaneTrimDv, the Max
            // Plane Trim slider) rather than searched for "however much closes the gap
            // completely" - a real plane MISMATCH is still the player's job to fly close in the
            // first place; this only trims the last bit away so the descent-phase steering doesn't
            // have to work so hard. 0 (the slider's floor) disables this entirely and reproduces
            // the old in-plane-only behavior exactly.
            //
            // JOINTLY re-optimizes burn TIME together with each sampled trim value, rather than
            // only sampling trim at the untrimmed search's own fixed-optimal time: bestUT above
            // was picked specifically to minimize error at dvNormal=0, so nudging the plane while
            // holding that exact time fixed usually makes things worse, not better, even when a
            // real plane mismatch exists and a different (time, trim) pair would genuinely help.
            // Each candidate trim value below gets its own small local time search (centered on
            // the untrimmed bestUT, not the whole orbit again - trim this small shouldn't move the
            // optimal time far) so it gets a fair, independent shot at beating the untrimmed
            // result instead of being evaluated at the wrong instant.
            //
            // SIGN CONVENTION: NormalBurnVector's positive direction (see ManeuverCreator.cs) is
            // assumed to match Cross(r, v) here (the standard orbital angular-momentum
            // convention). If plane trim ever makes the real in-game error worse instead of
            // better, that sign is the first thing to flip and re-test.
            if (maxPlaneTrimDv > 0 && bestIndex >= 0)
            {
                const int trimSamples = 9;
                const int localTimeSamples = 13;
                double untrimmedBestUT = bestUT;
                // +/-3 of the original grid's own step size - narrow (cheap: 13 samples instead
                // of another full-orbit 36), but wide enough that a trim-shifted optimal time a
                // few sample-steps away from the untrimmed one is still well within range.
                double localWindow = 6.0 * (searchDuration / (samples - 1));
                double localStart = untrimmedBestUT - localWindow / 2.0;

                for (int ti = 0; ti < trimSamples; ti++)
                {
                    double dvNormal = maxPlaneTrimDv * (2.0 * ti / (trimSamples - 1) - 1.0); // -max .. +max
                    if (dvNormal == 0)
                        continue; // already have the untrimmed result as the baseline in best*

                    double[] localErrors = new double[localTimeSamples];
                    bool[] localValid = new bool[localTimeSamples];
                    int localBestIndex = -1;
                    double localBestUT = 0, localBestDv = 0, localBestLon = 0, localBestLat = 0,
                        localBestErrorM = double.MaxValue, localBestImpactUT = 0;
                    // Same score/report split as the untrimmed search above.
                    double localBestScoredErrorM = double.MaxValue;

                    for (int i = 0; i < localTimeSamples; i++)
                    {
                        double candidateUT = localStart + localWindow * i / (localTimeSamples - 1);

                        if (TryEvaluateCandidate(orbit, body, candidateUT, targetPeriapsisRadius,
                                targetLatitudeDeg, targetLongitudeDeg, dvNormal,
                                out double dv, out double lon, out double lat, out double errorM, out double impactUT))
                        {
                            localValid[i] = true;
                            localErrors[i] = errorM;

                            double scoredErrorM = errorM + distance_penalty_per_second * (candidateUT - searchStartUT);
                            if (scoredErrorM < localBestScoredErrorM)
                            {
                                localBestScoredErrorM = scoredErrorM;
                                localBestErrorM = errorM;
                                localBestIndex = i;
                                localBestUT = candidateUT;
                                localBestDv = dv;
                                localBestLon = lon;
                                localBestLat = lat;
                                localBestImpactUT = impactUT;
                            }
                        }
                    }

                    if (localBestIndex < 0)
                        continue;

                    // Same parabola-vertex refinement as the untrimmed pass above, just against
                    // this trim value's own local samples.
                    if (localBestIndex > 0 && localBestIndex < localTimeSamples - 1
                        && localValid[localBestIndex - 1] && localValid[localBestIndex + 1])
                    {
                        double h = localWindow / (localTimeSamples - 1);
                        double y0 = localErrors[localBestIndex - 1];
                        double y1 = localErrors[localBestIndex];
                        double y2 = localErrors[localBestIndex + 1];
                        double denom = y0 - 2 * y1 + y2;

                        if (denom > 1e-9)
                        {
                            double offset = Math.Clamp(0.5 * (y0 - y2) / denom, -1.0, 1.0);
                            double refinedUT = localBestUT + offset * h;

                            if (TryEvaluateCandidate(orbit, body, refinedUT, targetPeriapsisRadius,
                                    targetLatitudeDeg, targetLongitudeDeg, dvNormal,
                                    out double dvR, out double lonR, out double latR, out double errorR, out double impactUTR))
                            {
                                double scoredErrorR = errorR + distance_penalty_per_second * (refinedUT - searchStartUT);
                                if (scoredErrorR < localBestScoredErrorM)
                                {
                                    localBestScoredErrorM = scoredErrorR;
                                    localBestErrorM = errorR;
                                    localBestUT = refinedUT;
                                    localBestDv = dvR;
                                    localBestLon = lonR;
                                    localBestLat = latR;
                                    localBestImpactUT = impactUTR;
                                }
                            }
                        }
                    }

                    if (localBestScoredErrorM < bestScoredErrorM)
                    {
                        bestScoredErrorM = localBestScoredErrorM;
                        bestErrorM = localBestErrorM;
                        bestUT = localBestUT;
                        bestDeltaV = localBestDv;
                        bestNormalDeltaV = dvNormal;
                        bestLon = localBestLon;
                        bestLat = localBestLat;
                        bestImpactUT = localBestImpactUT;
                    }
                }
            }

            return true;
        }

        static bool TryEvaluateCandidate(IKeplerPatch orbit, CelestialBodyComponent body, double candidateUT,
            double targetPeriapsisRadius, double targetLatitudeDeg, double targetLongitudeDeg, double dvNormal,
            out double dv, out double lon, out double lat, out double errorM, out double impactUT)
        {
            Vector3d r0 = orbit.GetRelativePositionAtUTZup(candidateUT);
            Vector3d v0 = orbit.GetOrbitalVelocityAtUTZup(candidateUT);

            dv = ComputeDeorbitDeltaV(r0.magnitude, v0.magnitude, body.gravParameter, targetPeriapsisRadius);
            Vector3d v1 = v0 + v0.normalized * dv;

            if (dvNormal != 0)
            {
                Vector3d orbitNormal = Vector3d.Cross(r0, v0).normalized;
                v1 += orbitNormal * dvNormal;
            }

            if (!PredictImpactLongitude(r0, v1, body, candidateUT, out lon, out lat, out impactUT))
            {
                errorM = 0;
                return false;
            }

            // Real ground distance to the target, not just a longitude difference.
            errorM = LandingPilot.HaversineDistanceMeters(lat, lon, targetLatitudeDeg, targetLongitudeDeg, body.radius);
            return true;
        }

        // A "shift node longitude by resonance orbit" deorbit-timing approach (an earlier version
        // of this search) is deliberately NOT implemented here: it's a direct port of Flight
        // Plan's algorithm (github.com/schlosrat/FlightPlan), and Flight Plan is GPLv3, which
        // cannot be incorporated into a CC BY-SA 4.0 project like K2D2Redux (compatibility only
        // runs CC BY-SA -> GPLv3, never the other direction). A resonance/phasing-orbit approach to
        // shifting a ground track's target-longitude crossing is standard, widely-published
        // orbital mechanics and is fine to reimplement independently from scratch if ever needed -
        // it's specifically porting Flight Plan's own implementation that must not happen.
    }
}
