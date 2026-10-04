using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using UnityEngine;
using K2D2.Controller;

namespace K2D2.Lift
{
    // Shared surface between the two selectable Ascent-phase controllers (Ascent = Guided,
    // ReactiveAscent = GravityTurn - see AscentProfile.cs). Pulled out so LiftPilot can hold one
    // "whichever profile is active right now" reference (LiftPilot.ascent) and so Adjust/LiftUI/
    // LiftAscentPath can keep reading altitude/apoapsis/climb-rate telemetry the exact same way
    // regardless of which profile actually flew the ascent.
    public abstract class AscentControllerBase : ExecuteController
    {
        // Current sea-level altitude, in km - refreshed by computeValues() every tick the Lift
        // page is visible or running (see LiftPilot.Update()).
        public float current_altitude_km = 0;

        // Current apoapsis altitude, in km.
        public float ap_km = 0;

        // How much apoapsis climbs per second of full throttle, smoothed - Adjust's own throttle
        // math (Adjust.cs) divides the remaining apoapsis error by this, so it needs to keep
        // meaning the same thing no matter which ascent profile measured it.
        public float delta_ap_per_second = 0;

        // Previous tick's ap_km, used by UpdateBasicTelemetry below to derive delta_ap_per_second.
        protected float last_ap_km = 0;

        // Refreshes current_altitude_km/ap_km/delta_ap_per_second/wanted throttle-relevant
        // telemetry. compute_delta_ap_per_second is false while this controller isn't the one
        // actually flying (see LiftPilot.Update()) - climb rate should only be measured while
        // this profile's throttle is actually the one commanding the engine.
        public abstract void computeValues(bool compute_delta_ap_per_second);

        // Common telemetry refresh both Ascent and ReactiveAscent need every tick - reads the
        // live orbit for altitude/apoapsis and smooths a climb-rate estimate from how fast
        // apoapsis is rising per second of full throttle. Each subclass's own computeValues()
        // calls this first, then adds whatever extra telemetry its own throttle/steering logic
        // needs on top (Ascent's wanted_elevation, ReactiveAscent's time_to_apoapsis_s).
        protected void UpdateBasicTelemetry(KSPVessel current_vessel, bool compute_delta_ap_per_second)
        {
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            ap_km = (float)(orbit.Apoapsis - orbit.referenceBody.radius) / 1000;
            current_altitude_km = (float)(current_vessel.GetSeaAltitude() / 1000);

            if (last_ap_km == 0)
            {
                last_ap_km = ap_km;
                return;
            }

            float delta_ap = ap_km - last_ap_km;
            last_ap_km = ap_km;

            if (!compute_delta_ap_per_second)
                return;

            float throttle = (float)current_vessel.GetThrottle();
            if (throttle > 0.1f && Time.deltaTime != 0)
            {
                float new_delta_ap_per_second = delta_ap / (Time.deltaTime * throttle);
                delta_ap_per_second = Mathf.Lerp(delta_ap_per_second, new_delta_ap_per_second, 0.1f);
            }
            else
                delta_ap_per_second = 0;
        }
    }
}
