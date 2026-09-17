using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing;
using KSP.Messages;
using KSP.Sim;
using KSP.Sim.Maneuver;
using KSP2FlightAssistant.MathLibrary;
using KTools;

// using KTools.UI;
// using K2D2.InfosPages;
using UnityEngine;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Node
{
    public class NodeExPilot : Pilot
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.NodeExecute");

        public static NodeExPilot Instance { get; set; }

        public ManeuverNodeData next_maneuver_node = null;
        public ManeuverNodeData execute_node = null;

        public NodeExSettings settings = new NodeExSettings();

        // Sub Pilots
        TurnTo turn;
        WarpTo warp;
        BurnManeuver burn;

        // Used both for auto-deleting execute_node once its burn is done (see nextMode()) and for
        // the two "Circularize at AP"/"Circularize at PE" quick-create buttons below.
        ManeuverCreator maneuver_creator = new ManeuverCreator();

        //  ExecuteController current_pilot = null;
        KSPVessel current_vessel;

        public SingleExecuteController current_executor = new SingleExecuteController();

        NodeExUI ui;

        public NodeExPilot()
        {
            Instance = this;
            debug_mode_only = false;

            _page = ui = new NodeExUI(this);

            K2D2PilotsMgr.Instance.RegisterPilot("Node", this);

            sub_contollers.Add(current_executor);
            current_vessel = K2D2_Plugin.Instance.current_vessel;

            GeneralTools.Game.Messages.Subscribe<VesselChangedMessage>(OnActiveVesselChanged);

            turn = new TurnTo();
            warp = new WarpTo();
            burn = new BurnManeuver();
        }

        public void OnActiveVesselChanged(MessageCenterMessage msg)
        {
            Stop();
        }

        public enum Mode
        {
            Off,
            Turn,
            Warp,
            Burn
        }

        public Mode mode = Mode.Off;

        public void setMode(Mode mode)
        {
            if (mode == this.mode)
                return;

            L.Log("setMode " + mode);  
            if (mode == Mode.Off)
            {
                TimeWarpTools.SetRateIndex(0, false);
                current_executor.setController(null);
                this.mode = mode;
                return;
            }

            if (next_maneuver_node == null)
                checkManeuver();

            if (next_maneuver_node == null)
            {
                Stop();
                return;
            }

            this.mode = mode;
            switch (mode)
            {
                case Mode.Off:
                    current_executor.setController(null);
                    break;
                case Mode.Turn:
                    // start
                    execute_node = next_maneuver_node;
                    current_executor.setController(turn);
                    turn.StartManeuver(execute_node);
                    break;
                case Mode.Warp:
                    if (!settings.auto_warp.V)
                    {
                        setMode(Mode.Burn);
                        return;
                    }
                    current_executor.setController(warp);
                    warp.StartManeuver(execute_node);
                    break;
                case Mode.Burn:
                    current_executor.setController(burn);
                    burn.StartManeuver(execute_node);
                    break;
            }

            L.Log("setMode " + mode);
        }

        public bool canStart()
        {
            if (next_maneuver_node == null)
                return false;

            var dt = GeneralTools.remainingStartTime(next_maneuver_node);
            if (dt < 0)
            {
                return false;
            }

            return true;
        }

        public void nextMode()
        {
            if (mode == Mode.Off)
            {
                Start();
                return;
            }
            if (mode == Mode.Burn)
            {
                // Auto-delete the node once its burn is done - there's nothing left to do with it.
                // RemoveNode, not RemoveAllNodes: a multi-node Flight Plan might have more queued
                // up behind this one, and checkManeuver() below picks the next one up once this
                // one's gone.
                maneuver_creator.Update();
                maneuver_creator.RemoveNode(execute_node);

                Stop();
                if (settings.pause_on_end.V)
                    TimeWarpTools.SetIsPaused(true);
                return;
            }

            var next = this.mode + 1;
            setMode(next);
        }

        public void UpdateUI()
        {
            if (!ui.isVisible) return;

            // F3(b): this is the SECOND per-frame UI path. It is reached from K2D2_Plugin.Update()
            // -> pilots_manager.UpdateControllers(), not from K2D2Window.Update(), so gating the
            // window alone would leave it alive. ui.isVisible only means "this is the selected tab"
            // - it stays true while the window is shut, so without this the node page kept running
            // the full Reset()/Status() sequence, flip-flop included, in flight with the window
            // closed.
            if (!K2D2_Plugin.IsWindowOpen) return;

            ui.status_bar.Reset();

            var st = ui.status_bar;
            if (!isRunning)
            {
                // K2's own face (see k2_avatar in node.uxml) now sits right next to this line, so
                // it no longer needs its own "K2:" text prefix to read as K2 talking - same for
                // every st.Warning()/st.Status() call in TurnTo.cs/BurnManeuvre.cs/WarpTo.cs below.
                if (next_maneuver_node == null)
                {
                    // Only worth showing while still true - a node plotted some other way since
                    // (e.g. by hand on the map) makes the old refusal moot.
                    if (circularize_error != null)
                        st.Warning(circularize_error);
                    else
                        st.Status("No Node Created");
                    return;
                }

                circularize_error = null;

                if (!valid_maneuver)
                {
                    st.Warning("Node data glitched");
                    st.Console("That's a known KSP2 map-loading glitch - pop open the map view and I'll pick it back up.");
                    return;
                }

                if (!canStart())
                {
                    st.Warning("That node's already in the past - replot it.");
                    return;
                }

                st.Status("Ready to execute node!");
            }
            else
                current_executor.updateUI(page.panel, st);
        }

        // Set by CreateCircularizeNode when it refuses to create a node (unbound orbit - see
        // that method) so UpdateUI can tell the player why nothing happened, instead of the
        // button silently doing nothing. Cleared on the next successful call.
        public string circularize_error = null;

        public bool valid_maneuver = false;

        public bool checkManeuver()
        {
            next_maneuver_node = current_vessel.GetNextManeuveurNode();
            valid_maneuver = false;
            if (next_maneuver_node == null)
            {
                Stop();
                return false;
            }

            double ut;

            var plan_solver = current_vessel.GetPlanSolver();
            if (plan_solver == null)
            {
                Stop();
                return false;
            }

            // check that the maneuver is well declared.
            Vector velocity_after_maneuver = plan_solver.GetVelocityAfterFirstManeuver(out ut);
            if (ut == 0)
            {
                // error
                Stop();
                return false;
            }

            valid_maneuver = true;
            return true;
        }

        // "Circularize at AP"/"Circularize at PE" quick-create buttons (Node tab). Only creates
        // the node - it doesn't drive Turn/Warp/Burn itself, the tab's existing Start button does
        // that once a node exists (same as a node the player plotted by hand on the map).
        //
        // Deliberately not ManeuverCreator.CircularizeOrbitApoapsis()/CircularizeOrbitPeriapsis()
        // above - those read orbit.Apoapsis/orbit.Periapsis off a "GetLastOrbit() as
        // PatchedConicsOrbit" cast, which throws InvalidCastException for the actively-flown vessel
        // under Redux (its orbit is a CurrentPatchedConicsOrbit - see
        // ManeuverCreator.CreateManeuverNodeAtUT's own comment). Uses the same state-vector-based
        // math as Landing's Circularize.cs and Lift's FinalCircularize instead, fed by
        // LandingTargeting's reusable static helpers rather than duplicating that math a third time.
        public void CreateCircularizeNode(bool atApoapsis)
        {
            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel == null) return;

            // Reset any other running pilot first - same as every other "start a pilot" path
            // (isRunning's setter above, LiftPilot.isRunning). Without this, clicking this button
            // while Lift or Landing was actively burning could wipe that pilot's own maneuver node
            // out from under it via RemoveAllNodesThenCreate below.
            K2D2_Plugin.ResetControllers();

            maneuver_creator.Update();

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;

            double now = GeneralTools.Current_UT;
            Vector3d r_now = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v_now = orbit.GetOrbitalVelocityAtUTZup(now);

            LandingTargeting.OrbitalElementsFromStateVectors(r_now, v_now, body.gravParameter,
                out double semiMajorAxis, out double apoapsisRadius, out double periapsisRadius);

            // Unbound (hyperbolic/parabolic) orbit - e.g. still inbound on an SOI capture, before
            // any capture burn has happened. semiMajorAxis comes out negative (or NaN) in that
            // case. Circularize at AP can't work here - a trajectory that never comes back has no
            // apoapsis - so refuse cleanly rather than create a NaN/garbage node. Circularize at PE
            // should still work: burning retrograde at periapsis to drop the far side back below
            // escape velocity is exactly how a capture-into-orbit burn works, and periapsis is
            // well-defined on a hyperbolic path (OrbitalElementsFromStateVectors returns a correct,
            // positive periapsisRadius either way). That case is handled separately below.
            bool unbound = double.IsNaN(semiMajorAxis) || semiMajorAxis <= 0;

            if (atApoapsis && unbound)
            {
                circularize_error = "Can't circularize at AP - this trajectory doesn't come back (still on an escape/capture path). Try Circularize at PE instead.";
                logger.LogInfo("[NodeExPilot] CreateCircularizeNode(AP): refused - " +
                    $"unbound orbit (semiMajorAxis={semiMajorAxis:n1})");
                return;
            }

            // Already past periapsis and still unbound means the trajectory is now outbound for
            // good (a capture window that's already closed) - r_now . v_now (radial velocity,
            // same sign convention as everywhere else in this class) is >= 0 once that's true.
            // Nothing to circularize at PE either in that case - refuse instead of quietly timing
            // a "burn right now" node against a periapsis that's already behind us.
            if (!atApoapsis && unbound && Vector3d.Dot(r_now, v_now) >= 0)
            {
                circularize_error = "Can't circularize at PE - already past periapsis on this trajectory, it's now outbound for good.";
                logger.LogInfo("[NodeExPilot] CreateCircularizeNode(PE): refused - " +
                    $"unbound and already outbound (semiMajorAxis={semiMajorAxis:n1})");
                return;
            }

            circularize_error = null;

            // period is only a real, meaningful number for a bound orbit - TimeToNextApoapsis/
            // TimeToNextPeriapsis below just need SOME positive search window to sweep across
            // either way, so on an unbound orbit (PE case only, by this point - AP already
            // returned above) fall back to SearchWindowFromStateVectors's synthetic one instead
            // (see its own comment).
            double period = unbound
                ? LandingTargeting.SearchWindowFromStateVectors(r_now, v_now, body.gravParameter)
                : LandingTargeting.OrbitalPeriodFromStateVectors(r_now, v_now, body.gravParameter);

            double burn_UT, deltaV;
            if (atApoapsis)
            {
                // Raise periapsis to meet the current apoapsis - burn at apoapsis.
                double time_to_apoapsis = LandingTargeting.TimeToNextApoapsis(r_now, v_now, body.gravParameter, period);
                burn_UT = now + time_to_apoapsis;

                double v_apoapsis = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, periapsisRadius, body.gravParameter);
                double v_circular = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, apoapsisRadius, body.gravParameter);
                deltaV = v_circular - v_apoapsis;
            }
            else
            {
                // Lower apoapsis to meet the current periapsis - burn at periapsis. Works
                // unchanged for the unbound/capture case too: VisVivaEquation.CalculateVelocity
                // recovers the same (correctly negative, for a hyperbolic orbit) semi-major axis
                // internally from (apoapsisRadius + periapsisRadius) / 2, so v_periapsis comes out
                // as the real hyperbolic speed at that point - CalculateVelocity's job here is
                // identical either way, it doesn't need to know the orbit is unbound at all.
                // deltaV comes out negative here (periapsis speed on an eccentric or hyperbolic
                // orbit is always higher than the circular speed at that same radius) -
                // CreateManeuverNodeAtUT's ProgradeBurnVector handles a negative value fine, it
                // just points retrograde.
                double time_to_periapsis = LandingTargeting.TimeToNextPeriapsis(r_now, v_now, body.gravParameter, period);
                burn_UT = now + time_to_periapsis;

                double v_periapsis = VisVivaEquation.CalculateVelocity(periapsisRadius, apoapsisRadius, periapsisRadius, body.gravParameter);
                double v_circular = VisVivaEquation.CalculateVelocity(periapsisRadius, periapsisRadius, periapsisRadius, body.gravParameter);
                deltaV = v_circular - v_periapsis;
            }

            logger.LogInfo($"[NodeExPilot] CreateCircularizeNode({(atApoapsis ? "AP" : "PE")}): " +
                $"now={now:n1} burn_UT={burn_UT:n1} (T+{burn_UT - now:n1}s) deltaV={deltaV:n2}m/s");

            maneuver_creator.RemoveAllNodesThenCreate(burn_UT, deltaV, created_node =>
            {
                checkManeuver();
            });
        }

        public override void Update()
        {
            checkManeuver();
            base.Update();

            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.O))
                isRunning = !isRunning;

            if (isRunning)
            {
                if (execute_node == null)
                {
                    Stop();
                    return;
                }

                //stagingController.CheckStaging();
                double UT = 0;
                switch (settings.start_mode.V)
                {
                    case NodeExSettings.StartMode.precise:
                        UT = execute_node.Time;
                        break;
                    case NodeExSettings.StartMode.half_duration:
                        UT = execute_node.Time - execute_node.BurnDuration / 2;
                        break;
                    case NodeExSettings.StartMode.constant:
                        UT = execute_node.Time - settings.start_before.V;
                        break;
                }

                warp.UT = UT;
                burn.UT = UT;
            }
            else
            {
           
            }
       
            if (current_executor.finished)
            {
                // auto next
                nextMode();
            }

            UpdateUI();
        }

  

        public override bool isRunning
        {
            get { return mode != Mode.Off; }
            set
            {
                if (value == base.isRunning)
                    return;
  
                if (!value)
                {
                    // stop
                    if (current_vessel != null)
                        current_vessel.SetThrottle(0);

                    setMode(Mode.Off);          
                }
                else
                {
                    // reset controller to desactivate other controllers.
                    K2D2_Plugin.ResetControllers();
                    TimeWarpTools.SetIsPaused(false);
                
                    setMode(Mode.Turn);
                }

                // send call backs
                base.isRunning = value; 
            }
        }

        public void Start()
        {
            isRunning = true;       
        }

        public void Stop()
        {
            isRunning = false;
        }

        public override void onReset()
        {
            Stop();
        }

        internal string ApiStatus()
        {
            string status = "";
        
            if (next_maneuver_node == null)
            {
                status = "No Maneuver Node";
            }
            else
            {
                if (!valid_maneuver) status = "Invalid Maneuver Node";
                // else if (!NodeExecute.Instance.canStart()) status = "No Future Maneuver Node";
                else if (isRunning)
                {
                    if (mode == Mode.Off) status = "Off";
                    else if (mode == Mode.Turn)
                    {
                        status = $"Turning: {current_executor.status_line}";
                        // report angle deviation?
                    }
                    else if (mode == Mode.Warp)
                    {
                        status = $"Warping: {current_executor.status_line}";
                        // report time to node?
                    }
                    else if (mode == Mode.Burn)
                    {
                        if (Game.UniverseModel.UniverseTime < next_maneuver_node.Time)
                        {
                            status = $"Waiting to Burn: {current_executor.status_line}";
                        }
                        else
                        {
                            status = $"Burning: {current_executor.status_line}";
                            // report burn remaining (delta-v?)
                        }
                    }
                }
                else status = "Done";
                // else status = "Unknown";
            }
            return status;
        }
    }
}
