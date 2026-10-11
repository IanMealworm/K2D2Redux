using System;
using K2D2.KSPService;
using K2D2.Lift;
using KSP.Sim;
using KSP.Sim.impl;
using UnityEngine;

namespace K2D2.Landing
{
    // Holds the vessel at an angle of attack and bank angle measured against the airflow - the
    // attitude Redux's impact prediction assumes stays constant when its Assumed attitude setting
    // is Current attitude (or SAS while SAS is in Stability Assist, which K2D2's lock uses).
    //
    // The axes, all in the body's rotating frame (where the air sits still, so the airflow is the
    // surface velocity):
    //  - flow: the direction of travel through the air.
    //  - lift_up: at right angles to the flow, in the vertical plane, on the side away from the
    //    ground. Lift along this stretches the flight; lift the other way shortens it.
    //  - side: at right angles to both.
    // The bank angle turns the "tilt direction" around the flow, starting from lift_up (0°) toward
    // side (+90°). The vessel's nose is held at angle_of_attack off retrograde, tilted toward that
    // direction, and its roll is pinned so the dorsal side faces it too.
    //
    // Which way the lift then actually points depends on the vessel's shape - a body flying
    // engines-first generally lifts away from the tilt. That's measured in flight rather than
    // assumed.
    //
    // Above the atmosphere Redux doesn't compare the vessel's attitude with the airflow where the
    // vessel is now: it coasts the orbit down to the top of the atmosphere and compares the
    // vessel's current orientation (fixed in space) with the airflow THERE
    // (ImpactPredictor.Begin -> ImpactAerodynamics.Capture). Held retrograde to the flow at the
    // vessel, it would predict an entry at whatever angle the orbit turns through before reaching
    // the air - tens of degrees from far out, which is why its landing point drifted between the
    // entry correction burns. HoldForEntry holds the attitude against the airflow at the entry point
    // instead, so the prediction is for the attitude actually wanted at entry.
    public class AtmoAttitudeHold
    {
        public Vector3d flow { get; private set; }
        public Vector3d lift_up { get; private set; }
        public Vector3d side { get; private set; }

        // Largest of the nose and roll errors, in degrees - Redux restarts its prediction whenever
        // the held attitude moves by more than 2°.
        public double attitude_error_deg { get; private set; }

        readonly KSPVessel current_vessel;

        public AtmoAttitudeHold()
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // Updates the airflow axes from the vessel's state. Returns false with nothing to hold
        // (no vessel, or barely moving through the air).
        public bool UpdateAxes(out Vector3d position, out Vector3d air_velocity, out ITransformFrame frame)
        {
            position = air_velocity = Vector3d.zero;
            frame = null;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return false;

            var model = new ImpactModel(vessel.mainBody, KTools.GeneralTools.Current_UT);
            model.StateAt(vessel.Orbit, model.epoch_UT, out position, out Vector3d inertial_velocity);
            frame = vessel.mainBody.transform.bodyFrame;
            return SetAxes(vessel.mainBody, position, inertial_velocity, out air_velocity);
        }

        // Axes from a state in ImpactModel's axes (the body frame's axes, at this moment).
        bool SetAxes(CelestialBodyComponent body, Vector3d position, Vector3d inertial_velocity, out Vector3d air_velocity)
        {
            Vector3d omega = body.transform.bodyFrame.ToLocalVector(body.relativeAngularVelocity);
            air_velocity = inertial_velocity - Vector3d.Cross(omega, position);
            if (air_velocity.magnitude < 1)
                return false;

            flow = air_velocity.normalized;
            Vector3d up = position.normalized;
            Vector3d vertical = up - flow * Vector3d.Dot(up, flow);
            if (vertical.magnitude < 1e-6)
            {
                // Falling straight down - any horizontal direction will do as the reference.
                Vector3d any = Math.Abs(flow.y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0);
                vertical = any - flow * Vector3d.Dot(any, flow);
            }
            lift_up = vertical.normalized;
            side = Vector3d.Cross(flow, lift_up);
            return true;
        }

        // Against the airflow the vessel is in now.
        public void Hold(double angle_of_attack_deg, double bank_deg)
        {
            if (UpdateAxes(out _, out _, out ITransformFrame frame))
                Lock(angle_of_attack_deg, bank_deg, frame);
        }

        // Against the airflow where the vessel's current orbit comes into the atmosphere (the
        // airflow here once it's inside).
        public void HoldForEntry(double angle_of_attack_deg, double bank_deg)
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;
            var model = new ImpactModel(vessel.mainBody, KTools.GeneralTools.Current_UT);
            model.StateAt(vessel.Orbit, model.epoch_UT, out Vector3d r, out Vector3d v);
            HoldForEntry(model, r, v, model.epoch_UT, angle_of_attack_deg, bank_deg);
        }

        // Against the airflow where the coast from (r, v) at UT - in model's axes, model made this
        // frame - comes into the atmosphere. For a trajectory that's only planned, such as the one
        // after a maneuver node. Falls back to the airflow here if that coast never gets there.
        internal void HoldForEntry(ImpactModel model, Vector3d r, Vector3d v, double UT,
            double angle_of_attack_deg, double bank_deg)
        {
            if (!model.EntryState(r, v, UT, out Vector3d entry_r, out Vector3d entry_v)
                || !SetAxes(model.body, entry_r, entry_v, out _))
            {
                Hold(angle_of_attack_deg, bank_deg);
                return;
            }
            Lock(angle_of_attack_deg, bank_deg, model.body.transform.bodyFrame);
        }

        // For a ballistic prediction (no lift). With Redux's Assumed attitude set to SAS, SAS's own
        // Retrograde mode makes Redux assume exactly retrograde at entry whatever the vessel is
        // doing - a held attitude's last degree or two of wobble moved the predicted landing point
        // by several km on the test flights. Otherwise it holds retrograde to the entry airflow.
        public void HoldBallistic()
        {
            if (!HoldSasRetrograde())
                HoldForEntry(0, 0);
        }

        // The same, for a trajectory that's only planned (see the HoldForEntry overload).
        internal void HoldBallistic(ImpactModel model, Vector3d r, Vector3d v, double UT)
        {
            if (!HoldSasRetrograde())
                HoldForEntry(model, r, v, UT, 0, 0);
        }

        bool HoldSasRetrograde()
        {
            if (!ReduxImpactPrediction.FollowsSas() || current_vessel?.Autopilot == null)
                return false;
            var autopilot = current_vessel.Autopilot;
            autopilot.Enabled = true;
            if (autopilot.SAS != null)
                autopilot.SAS.lockedMode = false;
            if (autopilot.AutopilotMode != AutopilotMode.Retrograde)
                autopilot.SetMode(AutopilotMode.Retrograde);
            // Redux doesn't look at where the vessel points in this mode, so there's nothing to
            // wait for.
            attitude_error_deg = 0;
            return true;
        }

        void Lock(double angle_of_attack_deg, double bank_deg, ITransformFrame frame)
        {
            double bank = bank_deg * Math.PI / 180;
            double alpha = angle_of_attack_deg * Math.PI / 180;
            Vector3d tilt = lift_up * Math.Cos(bank) + side * Math.Sin(bank);
            Vector3d nose = -flow * Math.Cos(alpha) + tilt * Math.Sin(alpha);
            Vector3d dorsal = tilt - nose * Vector3d.Dot(tilt, nose);

            var autopilot = current_vessel.Autopilot;
            autopilot.Enabled = true;
            if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                autopilot.SetMode(AutopilotMode.StabilityAssist);
            // Same full-orientation lock as Lift's roll program (see Ascent's class comment): it
            // pins the nose and the roll at once.
            autopilot.SAS.LockRotation(new Rotation(frame, Ascent.RotationFromNoseAndDorsal(nose, dorsal)));
            autopilot.SAS.lockedMode = true;

            Rotation current = Rotation.Reframed(current_vessel.GetRotation(), frame);
            Vector3d current_nose = (current.localRotation * Vector3.up).normalized;
            Vector3d current_dorsal = (current.localRotation * Vector3.forward).normalized;
            double nose_error = Vector3d.Angle(nose, current_nose);
            Vector3d dorsal_flat = current_dorsal - nose * Vector3d.Dot(current_dorsal, nose);
            double roll_error = dorsal_flat.magnitude > 1e-6 ? Vector3d.Angle(dorsal, dorsal_flat) : 0;
            attitude_error_deg = Math.Max(nose_error, roll_error);
        }

        public void Release()
        {
            var autopilot = current_vessel?.Autopilot;
            if (autopilot?.SAS != null)
                autopilot.SAS.lockedMode = false;
        }
    }
}
