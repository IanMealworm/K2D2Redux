
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

        // Both ascent controllers are built up front; the dropdown picks which one flies, and
        // that choice is fixed for the run (run_ascent) so changing it mid-flight can't swap the
        // controller under the burn.
        Ascent ascent_guided = null;
        ReactiveAscent ascent_gravity_turn = null;
        Adjust adjust = null;
        Coasting coasting = null;
        FinalCircularize final_circularize;
        AscentControllerBase run_ascent = null;

        internal AscentControllerBase ascent => run_ascent ?? SelectedAscent();

        AscentControllerBase SelectedAscent() =>
            settings.ascent_profile.V == AscentProfile.GravityTurn
                ? (AscentControllerBase)ascent_gravity_turn
                : ascent_guided;

        public ExecuteController current_subpilot = null;

        public LiftPilot()
        {
            settings = new LiftSettings();
            ascent_path = new LiftAscentPath(settings);
            _page = new LiftUI(this);

            current_vessel = K2D2_Plugin.Instance.current_vessel;
       
            ascent_guided = new Ascent(settings, ascent_path);
            ascent_gravity_turn = new ReactiveAscent(settings);
            adjust = new Adjust(settings, this);
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
                // The roll program can still hold SAS in locked-rotation mode when the ascent
                // ends; the prograde holds that follow need it off.
                if (_status == LiftStatus.Ascent)
                    ReleaseRollLock();
                _status = value;
                switch (value)
                {
                    case LiftStatus.Off:
                    {
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
                        run_ascent = null;
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

                base.isRunning = value; 
            }
        }

        void OnStartController()
        {
            run_ascent = SelectedAscent();
            status = LiftStatus.Ascent;
        }

        void ReleaseRollLock()
        {
            var autopilot = current_vessel?.Autopilot;
            if (autopilot != null)
                autopilot.SAS.lockedMode = false;
        }

        internal bool result_ok = false;
        internal string end_status;

        public void EndLiftPilot(bool result_ok, string end_status)
        {
            this.result_ok = result_ok;
            this.end_status = end_status;
            // Through the isRunning setter, not status directly, so is_running_event fires and the
            // Start button and K2's avatar update.
            isRunning = false;
        }

        public WarpTo warp_to = new WarpTo();


        public void NextMode()
        {
            // Without auto-circularize the run ends once apoapsis is final (after Adjust), leaving
            // the circularization to the player.
            if (status == LiftStatus.Adjust && !settings.auto_circularize.V)
            {
                if (settings.pause_on_final.V)
                {
                    TimeWarpTools.SetRateIndex(0, false);
                    TimeWarpTools.SetIsPaused(true);
                }
                EndLiftPilot(true, "Ascent complete - plan your circularization at apoapsis");
                return;
            }
            status = status.Next();
        }

        public override void Update()
        {
        
            if (current_vessel == null) return;

            if (isRunning)
                ascent.computeValues(status == LiftStatus.Ascent);
            else if (page.isVisible)
                ascent.computeIdleValues(current_vessel);

            if (!isRunning)
                return;

            var subpilot = current_subpilot;
            if (subpilot != null)
            {
                subpilot.Update();

                // Update() can end the run itself (EndLiftPilot), which clears current_subpilot.
                if (current_subpilot == subpilot && subpilot.finished)
                    NextMode();
            }
        }
    }
}
