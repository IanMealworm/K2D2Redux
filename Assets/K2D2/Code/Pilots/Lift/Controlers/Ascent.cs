
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;
namespace K2D2.Lift
{
    /// <summary>
    /// rotation used for docking
    /// </summary>
    public class Ascent : ExecuteController
    {
        LiftSettings settings = null;
        LiftAscentPath ascent_path = null;

        KSPVessel current_vessel;

        public Ascent(LiftSettings lift_settings, LiftAscentPath ascent_path)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.settings = lift_settings;
            this.ascent_path = ascent_path;
        }

        public float current_altitude_km = 0;
        public float ap_km = 0;
        float last_ap_km = 0;
        public float delta_ap_per_second;
        float wanted_elevation;

        float wanted_throttle = 0;

        float heading_correction = 0;
        float h_speed_heading = 0;

        public override void Start()
        {
            base.Start();

            SASTool.setAutoPilot(AutopilotMode.StabilityAssist);
            last_ap_km = 0;
            ap_km = 0;
            delta_ap_per_second = 0;
            wanted_elevation = -90;
        }

        public void computeValues(bool compute_delta_ap_per_second)
        {
            if (current_vessel.VesselComponent == null)
                return;

            // FOURTEENTH follow-up fix (see NOTICE.md): the comment this replaced said the cast to the
            // concrete PatchedConicsOrbit was "VERIFIED" - that was true for orbits in general, but not
            // for the actively-flown vessel. IL/metadata inspection of Assembly-CSharp.dll showed
            // KSP.Sim.impl.PatchedConicsOrbit is NOT the only class implementing KSP.Sim.IKeplerPatch -
            // Redux.Ecs.Components.CurrentPatchedConicsOrbit also implements it, as a completely
            // unrelated sibling class (both extend System.Object directly, neither derives from the
            // other). Redux's ECS layer hands back a CurrentPatchedConicsOrbit for the vessel currently
            // being simulated/flown - exactly this vessel, exactly while the Lift autopilot is running -
            // so the hard cast below threw InvalidCastException on every single Update(), which is why
            // the Lift autopilot didn't work at all. Apoapsis and referenceBody are both members of the
            // IOrbit interface (which IKeplerPatch extends), so no concrete cast is needed - just read
            // them straight off the interface VesselComponent.Orbit already returns.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            ap_km = (float)(orbit.Apoapsis - orbit.referenceBody.radius) / 1000;
            current_altitude_km = (float)(current_vessel.GetSeaAltitude() / 1000);

            if (last_ap_km == 0)
            {
                last_ap_km = ap_km;
            }
            else
            {
                float delta_ap = ap_km - last_ap_km;
                last_ap_km = ap_km;
                if (compute_delta_ap_per_second)
                {
                    // compute delta_ap_per_second only on ascent
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

            wanted_elevation = ascent_path.compute_elevation(current_altitude_km);
        }

        public void applyDirection()
        {
            var autopilot = current_vessel.Autopilot;

            if (autopilot == null)
                return;

            // force autopilot
            autopilot.Enabled = true;

            var telemetry = SASTool.getTelemetry();
            var up = telemetry.HorizonUp;

            if (settings.heading_correction.V)
            {
                computeSpeedHeading();
            }
            else
                heading_correction = 0;


            Vector3d direction = QuaternionD.Euler(-wanted_elevation, settings.heading.V + heading_correction, 0) * Vector3d.forward;
            Vector direction_vector = new Vector(up.coordinateSystem, direction);

            autopilot.SAS.lockedMode = false;
            autopilot.SAS.SetTargetOrientation(direction_vector, false);
        }

        void computeSpeedHeading()
        {
            // use Up local coordinate as reference frame
            var Upcoords = current_vessel.VesselVehicle.Up.coordinateSystem;
            var SurfaceVelocity = Vector.Reframed(current_vessel.VesselVehicle.SurfaceVelocity, Upcoords).vector;
            var North = Vector.Reframed(current_vessel.VesselVehicle.North, Upcoords).vector;
            var Up = current_vessel.VesselVehicle.Up.vector;

            var UpSpeed = Up.normalized * Vector3d.Dot(SurfaceVelocity, Up);
            var LocalHSpeed = SurfaceVelocity - UpSpeed;

       
            h_speed_heading = (float)-Vector3d.SignedAngle(LocalHSpeed.normalized, North, Up);


            heading_correction = GeneralTools.diffAngle(settings.heading.V, h_speed_heading);

            if (heading_correction > 45)
                heading_correction = 45;
            else if (heading_correction < -45)
                heading_correction = -45;
        }


        // Ascent has no narrative feedback of its own beyond LiftUI's generic "Status : Ascent"
        // headline - all its telemetry moved to UpdateInfoRows below (LIFT INFO table) instead of
        // scrolling through the console text.
        public override void updateUI(VisualElement root_el, FullStatus st)
        {
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Apoapsis Alt.", $"{ap_km:n2} km");
            addRow("Altitude", $"{current_altitude_km:n2} km");
            addRow("Climb Rate", $"{delta_ap_per_second:n2} km/s");
            addRow("Pitch Target", $"{wanted_elevation:n2} °");
            addRow("Throttle", $"{wanted_throttle:n2}");

            if (settings.heading_correction.V)
            {
                addRow("Surface Heading", $"{h_speed_heading:n2} °");
                addRow("Heading Correction", $"{heading_correction:n2} °");
            }
        }

        public override void Update()
        {
            applyDirection();
            finished = false;
            float remaining_Ap = settings.destination_Ap_km.V - ap_km;
            if (remaining_Ap <= settings.end_ascent_error)
            {
                finished = true;
                return;
            }
            else
            {
                if (delta_ap_per_second <= 0)
                {
                    wanted_throttle = settings.max_throttle.V;
                }
                else
                {
                    wanted_throttle = remaining_Ap / delta_ap_per_second;
                    if (wanted_throttle > settings.max_throttle.V)
                        wanted_throttle = settings.max_throttle.V;
                }

                current_vessel.SetThrottle(wanted_throttle);
            }
        }


    }
}
