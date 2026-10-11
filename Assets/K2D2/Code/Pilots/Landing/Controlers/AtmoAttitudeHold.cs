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
        public double nose_error_deg { get; private set; }
        public double roll_error_deg { get; private set; }

        // How far the nose moved against the airflow over the last drift_window_s - measured only
        // since the attitude asked for last changed, so a vessel still swinging round to a new
        // attitude doesn't count as steady (NaN until then). A vessel that can't quite reach the attitude asked for - aerodynamic forces
        // stronger than its reaction wheels, or no way to roll with the engines off - can still sit
        // steadily a few degrees off it, which is all Redux needs: on two booster flights the
        // entry guidance waited the whole way down for an attitude error under 2° and never steered.
        public double nose_drift_deg { get; private set; } = double.NaN;
        const double drift_window_s = 1;
        const double steady_drift_deg = 0.5;
        const double max_steady_offset_deg = 15;
        Vector3d drift_reference;
        double drift_reference_UT = double.NaN;
        // The nose asked for, against the airflow; a change bigger than this restarts the drift
        // measurement.
        Vector3d last_wanted_nose;
        const double wanted_change_deg = 0.5;

        // Holding the attitude asked for, or steady close enough to it.
        public bool Settled(double tolerance_deg) =>
            attitude_error_deg < tolerance_deg
            || (nose_drift_deg < steady_drift_deg && nose_error_deg < max_steady_offset_deg);

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

        // Against the airflow where the vessel's coast comes back down through altitude_m - for a
        // stage still climbing after its boost-back. Its burn leaves it pointing back toward the
        // target; the airflow right there is close to the other way, but the one it will be
        // falling through is close to straight up - a much smaller turn, and the right way round
        // already. (Turning to the airflow where it was took the test booster 60-80 s on reaction
        // wheels alone, half of it turning one way only to swing back as the fall steepened.)
        public void HoldForDescent(double altitude_m, double angle_of_attack_deg, double bank_deg)
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;
            var model = new ImpactModel(vessel.mainBody, KTools.GeneralTools.Current_UT);
            model.StateAt(vessel.Orbit, model.epoch_UT, out Vector3d r, out Vector3d v);
            if (!model.DescentState(r, v, model.epoch_UT, vessel.mainBody.radius + altitude_m, out Vector3d entry_r, out Vector3d entry_v)
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

        // Points the nose (where the engines push) along `direction` - in ImpactModel's axes, i.e.
        // the body frame's axes at this moment - keeping whatever roll the vessel has, since a burn
        // doesn't care about it and rolling a big booster round only slows its turn. For burns
        // steered by K2D2 itself rather than through a maneuver node.
        public void HoldDirection(Vector3d direction)
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null || direction.magnitude < 1e-9)
                return;
            ITransformFrame frame = vessel.mainBody.transform.bodyFrame;
            Vector3d nose = direction.normalized;
            Rotation current = Rotation.Reframed(current_vessel.GetRotation(), frame);
            Vector3d current_dorsal = (current.localRotation * Vector3.forward).normalized;
            Vector3d dorsal = current_dorsal - nose * Vector3d.Dot(current_dorsal, nose);
            if (dorsal.magnitude < 1e-3)
            {
                Vector3d any = Math.Abs(nose.y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0);
                dorsal = any - nose * Vector3d.Dot(any, nose);
            }
            Lock(nose, dorsal.normalized, frame);
        }

        void Lock(double angle_of_attack_deg, double bank_deg, ITransformFrame frame)
        {
            double bank = bank_deg * Math.PI / 180;
            double alpha = angle_of_attack_deg * Math.PI / 180;
            Vector3d tilt = lift_up * Math.Cos(bank) + side * Math.Sin(bank);
            Vector3d nose = -flow * Math.Cos(alpha) + tilt * Math.Sin(alpha);
            Vector3d dorsal = tilt - nose * Vector3d.Dot(tilt, nose);
            if (keep_roll)
            {
                // Whatever roll the vessel had when this attitude was first asked for, held from
                // then on.
                if (double.IsNaN(kept_for_bank) || Math.Abs(LandingTargeting.WrapDegrees180(bank_deg - kept_for_bank)) > 0.5
                    || Math.Abs(angle_of_attack_deg - kept_for_aoa) > 0.5)
                {
                    Rotation current = Rotation.Reframed(current_vessel.GetRotation(), frame);
                    kept_dorsal = (current.localRotation * Vector3.forward).normalized;
                    kept_for_bank = bank_deg;
                    kept_for_aoa = angle_of_attack_deg;
                }
                Vector3d flat = kept_dorsal - nose * Vector3d.Dot(kept_dorsal, nose);
                if (flat.magnitude > 1e-3)
                    dorsal = flat;
            }
            Lock(nose, dorsal.normalized, frame);
        }

        // For a vessel whose roll doesn't matter (a round booster: its lift goes with the tilt of
        // its nose, whichever way it's rolled), the roll is left as it is rather than turned to
        // match the bank - with the lift steered as a vector, small changes can swing the bank a
        // long way, and rolling a long booster round takes a while.
        public bool keep_roll;
        Vector3d kept_dorsal;
        double kept_for_bank = double.NaN, kept_for_aoa;

        void Lock(Vector3d nose, Vector3d dorsal, ITransformFrame frame)
        {
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
            nose_error_deg = Vector3d.Angle(nose, current_nose);
            Vector3d dorsal_flat = current_dorsal - nose * Vector3d.Dot(current_dorsal, nose);
            roll_error_deg = dorsal_flat.magnitude > 1e-6 ? Vector3d.Angle(dorsal, dorsal_flat) : 0;
            attitude_error_deg = Math.Max(nose_error_deg, roll_error_deg);

            Vector3d nose_against_air = new Vector3d(Vector3d.Dot(current_nose, flow), Vector3d.Dot(current_nose, lift_up),
                Vector3d.Dot(current_nose, side));
            double now = KTools.GeneralTools.Current_UT;
            Vector3d wanted_against_air = new Vector3d(Vector3d.Dot(nose, flow), Vector3d.Dot(nose, lift_up), Vector3d.Dot(nose, side));
            if (Vector3d.Angle(wanted_against_air, last_wanted_nose) > wanted_change_deg)
            {
                last_wanted_nose = wanted_against_air;
                nose_drift_deg = double.NaN;
                drift_reference_UT = double.NaN;
            }
            if (double.IsNaN(drift_reference_UT) || now - drift_reference_UT >= drift_window_s)
            {
                if (!double.IsNaN(drift_reference_UT))
                    nose_drift_deg = Vector3d.Angle(drift_reference, nose_against_air);
                drift_reference = nose_against_air;
                drift_reference_UT = now;
            }
        }

        public void Release()
        {
            var autopilot = current_vessel?.Autopilot;
            if (autopilot?.SAS != null)
                autopilot.SAS.lockedMode = false;
        }
    }
}
