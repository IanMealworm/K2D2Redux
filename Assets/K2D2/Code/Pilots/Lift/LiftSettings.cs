using K2UI;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;

namespace K2D2.Lift
{
    public class LiftSettings
    {
        public ClampSetting<float> heading = new ("lift.heading", 90,  0 , 360);
 
        public Setting<int> start_altitude_km = new ("lift.start_altitude_km", 2);

        public ClampSetting<float> mid_rotate_ratio = new ("lift.mid_rotate_ratio", 0.2f, 0, 1);

        public ClampSetting<float> end_rotate_ratio = new ("lift.end_rotate_ratio", 0.5f, 0, 1);

        public LiftSettings()
        {
            start_altitude_km.listeners += v => update_altitudes();
            mid_rotate_ratio.listeners += v => update_altitudes();
            end_rotate_ratio.listeners += v => update_altitudes();
            destination_Ap_km.listeners += v => update_altitudes();

            update_altitudes();
        }

        public void update_altitudes()
        {
            _mid_rotate_altitude_km = Mathf.Lerp(start_altitude_km.V, destination_Ap_km.V, mid_rotate_ratio.V);
            _end_rotate_altitude_km = Mathf.Lerp(start_altitude_km.V, destination_Ap_km.V, end_rotate_ratio.V);
        }

        float _mid_rotate_altitude_km = -1;
        public float mid_rotate_altitude_km
        {
            get {
                return _mid_rotate_altitude_km;
            }
        }

        float _end_rotate_altitude_km = -1;
        public float end_rotate_altitude_km
        {
            get {
                return _end_rotate_altitude_km;
            }
        }

        public Setting<int> destination_Ap_km = new("lift.destination_Ap_km", 100);

        public ClampSetting<float> max_throttle = new ("lift.max_throttle", 1, 0, 1);
        public ClampSetting<float> end_ascent_pc = new ("lift.end_ascent_pc", 0.1f, 0, 1);

        public float end_ascent_error
        {
            get => destination_Ap_km.V * end_ascent_pc.V/100;
        }

        public Setting<bool> coasting_warp = new ("lift.coasting_warp", true);
        public Setting<bool> adjust = new ("lift.adjust", true);
        public ClampSetting<float> end_adjust_pc = new ("lift.end_adjust_pc", 0.01f, 0.001f, 0.5f);

        public float end_adjust_error
        {
            get => destination_Ap_km.V * end_adjust_pc.V/100;
        }

        // Default on: run straight through ascent -> atmosphere clear -> circularize with no
        // pause, since FinalCircularize (Controlers/Final.cs) now creates and flies the
        // circularize burn itself instead of waiting on a manually- or FlightPlan-created node.
        // Turning this off falls back to the old flow - Coasting.cs only actually pauses on
        // pause_on_final below when this is off.
        public Setting<bool> auto_circularize = new ("lift.auto_circularize", true);

        public Setting<bool> pause_on_final = new ("lift.pause_on_final", true);

        public Setting<bool> heading_correction = new ("lift.heading_correction", true);

        // ROLL PROGRAM (see Ascent.cs's own comment for the full reasoning). Lives in the Profile
        // foldout alongside Heading, not ADVANCED, per Reese - it's a per-launch choice like
        // heading is, not a tuning knob. Off by default; measured relative to the vessel's OWN
        // launch orientation rather than an absolute compass/navball reading (see Ascent.cs).
        public Setting<bool> roll_program = new("lift.roll_program", false);
        public ClampSetting<float> roll_program_altitude_km = new("lift.roll_program_altitude_km", 0.1f, 0, 5);
        public ClampSetting<float> roll_program_angle_deg = new("lift.roll_program_angle_deg", 0, -180, 180);

        // How fast Ascent.cs's roll ramp (ramped_roll_target_deg) is allowed to advance toward
        // roll_program_angle_deg - used to be a hardcoded RollProgramMaxRateDegPerSec constant
        // (3 deg/s, picked during tuning to avoid the SAS overshoot/oscillation described in
        // Ascent.cs's class comment). Reese asked to be able to tune it himself instead - some
        // vessels can probably tolerate a faster roll than others without overshooting. Default
        // and range centered on that same 3 deg/s that was already flight-tested and known to work.
        public ClampSetting<float> roll_program_rate_deg_s = new("lift.roll_program_rate_deg_s", 3f, 0.5f, 15f);

        Label end_ascent_alt;
        Label end_adjust_alt;

        Group adjust_group;

        void setLabels()
        {
            end_ascent_alt.text = $"End Ascent Alt. : {destination_Ap_km.V - end_ascent_error:n2} km";
            end_adjust_alt.text = $"Final Adjust Alt : {destination_Ap_km.V - end_adjust_error:n2} km";
        }

        public void setupUI(VisualElement root)
        {
            end_ascent_alt =  root.Q<Label>("end_ascent_alt");
            end_adjust_alt = root.Q<Label>("end_adjust_alt");
            adjust_group = root.Q<K2UI.Group>("adjust_group");

            root.Q<K2Toggle>("heading_correction").Bind(heading_correction);  

            // bind to settings and add listener on the same line !
            root.Q<K2Slider>("end_ascent_pc").Bind(end_ascent_pc);
            end_ascent_pc.listen(v => setLabels());

            root.Q<K2Toggle>("coasting_warp").Bind(coasting_warp);

            // adjust is binded to setting and to show adjust group 
            root.Q<K2Toggle>("adjust").Bind(adjust);
            adjust.listen(v => adjust_group.Show(v));

            root.Q<K2Slider>("end_adjust_pc").Bind(end_adjust_pc);
            end_adjust_pc.listen(v => setLabels());

            root.Q<K2Toggle>("auto_circularize").Bind(auto_circularize);

            root.Q<K2Toggle>("pause_on_final").Bind(pause_on_final);

            // ROLL PROGRAM - .listen(), not .listeners += (see LandingSettings.setupUI's own
            // comment on precision_landing for exactly why that distinction matters): this
            // defaults to off, so a plain .listeners += would leave roll_program_settings visible
            // on a fresh window open until the toggle was flipped once, same bug Reese hit on
            // Landing's Precision Landing toggle.
            root.Q<K2Toggle>("roll_program").Bind(roll_program);
            var roll_program_settings = root.Q<VisualElement>("roll_program_settings");
            roll_program.listen(v => roll_program_settings.Show(v));
            roll_program_settings.Q<K2Slider>("roll_program_altitude_km").Bind(roll_program_altitude_km);
            roll_program_settings.Q<K2Slider>("roll_program_angle_deg").Bind(roll_program_angle_deg);
            roll_program_settings.Q<K2Slider>("roll_program_rate_deg_s").Bind(roll_program_rate_deg_s);
        }
    }
}
