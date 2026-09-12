
using K2D2.KSPService;
using KTools;
using K2D2.Controller;
using K2D2.Node;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Lift
{
    public class LiftPilot : Pilot
    {
        public enum LiftStatus
        {
            Off,
            Ascent,
            Coasting,
            Adjust,
            Circularize
        }

        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.Lift");

        public static LiftPilot Instance { get; set; }

        internal  LiftSettings settings;

        internal  LiftAscentPath ascent_path = null;

        KSPVessel current_vessel;

        Vector3d direction = Vector3d.zero;

        // sub pilots
        internal Ascent ascent = null;
        Adjust adjust = null;
        Coasting coasting = null;   
        FinalCircularize final_circularize;

        public ExecuteController current_subpilot = null;

        public LiftPilot()
        {
            settings = new LiftSettings();
            ascent_path = new LiftAscentPath(settings);
            _page = new LiftUI(this);

            current_vessel = K2D2_Plugin.Instance.current_vessel;
       
            ascent = new Ascent(settings, ascent_path);
            adjust = new Adjust(settings, ascent);
            coasting = new Coasting(this, settings);
            final_circularize = new FinalCircularize(this, settings);
        
            Instance = this;

            debug_mode_only = false;

            K2D2PilotsMgr.Instance.RegisterPilot("Lift", this);
        }

        public override void onReset()
        {
            isRunning = false;
        }

        LiftStatus _status = LiftStatus.Off;
        internal LiftStatus status
        {
            get { return _status; }
            set
            {
                if (_status == value)
                    return;
                _status = value;
                switch (value)
                {
                    case LiftStatus.Off:
                    {
                        // stop
                        if (current_vessel != null)
                        {
                            current_vessel.SetThrottle(0);
                            // Roll program (see Ascent.cs) drives this as a raw control axis input,
                            // not through SAS - if the pilot gets stopped mid-correction, release it
                            // here too so a stray roll command doesn't keep firing after the
                            // autopilot itself has stopped.
                            current_vessel.Roll = 0;
                        }

                        current_subpilot = null;
                    }
                        break;
                    case LiftStatus.Ascent:
                        current_subpilot = ascent;
                        break;
                    case LiftStatus.Coasting:
                        current_subpilot = coasting;
                        break;
                    case LiftStatus.Adjust:
                        current_subpilot = adjust;
                        break;
                    case LiftStatus.Circularize:
                        current_subpilot = final_circularize;
                        break;
                }

                if (current_subpilot != null)
                    current_subpilot.Start();
            }
        }

        public override bool isRunning
        {
            get { return _status != LiftStatus.Off; }
            set
            {
                if (value == isRunning)
                    return;

                if (!value)
                {
                    status = LiftStatus.Off;
                }
                else
                {
                    // reset controller to desactivate other controllers.
                    K2D2_Plugin.ResetControllers();
                    OnStartController();
                }

                // send call backs
                base.isRunning = value; 
            }
        }

        void OnStartController()
        {
            status = LiftStatus.Ascent;
        }

        internal bool result_ok = false;
        internal string end_status;

        public void EndLiftPilot(bool result_ok, string end_status)
        {
            this.result_ok = result_ok;
            this.end_status = end_status;
            // Was: status = LiftStatus.Off directly. That flips the getter's answer
            // (isRunning reads _status != Off) but skips the isRunning SETTER entirely, so
            // base.isRunning's own backing field never gets updated and is_running_event never
            // fires. Node/Landing end their run via isRunning = false (see NodeExPilot.Stop(),
            // LandingPilot's touchdown/abort paths) for exactly this reason - anything bound to
            // is_running_event (LiftUI's run_button toggle state, K2's avatar SetRunning) needs
            // that callback to update, or it stays stuck showing "running" after the pilot has
            // actually finished on its own (as opposed to being stopped via the Brake/Start
            // button, which already goes through this setter).
            isRunning = false;
        }

        public WarpTo warp_to = new WarpTo();


        public void NextMode()
        {
            status = status.Next();
        }

        public override void Update()
        {
        
            if (current_vessel == null) return;

            if (isRunning || page.isVisible)
                ascent.computeValues(status == LiftStatus.Ascent);

            if (!isRunning)
                return;

            var subpilot = current_subpilot;
            if (subpilot != null)
            {
                subpilot.Update();

                // A subpilot's Update() can end the whole run right here - Coasting's Ap-under-
                // atmosphere-limit check and FinalCircularize's burn-complete case (see Final.cs)
                // both call lift.EndLiftPilot() directly rather than just setting finished, so
                // that is_running_event actually fires (see EndLiftPilot's own comment about why
                // it goes through the isRunning setter instead of setting status directly). That
                // synchronously nulls current_subpilot via the status setter's Off case, before
                // we get back here. Re-checking the field itself (not the stale local) means we
                // don't NullReferenceException on a null current_subpilot, and don't call
                // NextMode() a second time on top of a run that already ended itself.
                if (current_subpilot == subpilot && subpilot.finished)
                    NextMode();
            }
        }
    }
}
