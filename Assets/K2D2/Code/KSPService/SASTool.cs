
using System.Reflection;
using KSP.Sim;
using KSP.Sim.impl;

using KTools;

namespace K2D2
{
    public class SASTool
    {
        // These are private in the game. Looking them up is the slow part of reflection, and
        // getTelemetry runs several times a frame while a pilot steers, so it's done once here.
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sas_response_field = typeof(VesselSAS).GetField("sasResponse", Private);
        static readonly FieldInfo angular_delta_field = typeof(VesselSAS).GetField("angularDelta", Private);
        static readonly FieldInfo telemetry_field = typeof(VesselAutopilot).GetField("_telemetry", Private);

        public static VesselAutopilot currentAutoPilot()
        {
            return K2D2_Plugin.Instance.current_vessel?.VesselComponent?.Autopilot;
        }

        public static VesselSAS currentSas()
        {
            return K2D2_Plugin.Instance.current_vessel?.VesselComponent?.Autopilot?.SAS;
        }

        public static double getSasResponsePC()
        {
            var sas = currentSas();
            if (sas == null || sas_response_field == null)
                return 0;

            return ((Vector3d)sas_response_field.GetValue(sas)).magnitude * 100;
        }

        public static Vector3d geSASAngularDelta()
        {
            var sas = currentSas();
            if (sas == null || angular_delta_field == null)
                return Vector3d.zero;

            return GeneralTools.correctEuler((Vector3d)angular_delta_field.GetValue(sas));
        }

        public static TelemetryComponent getTelemetry()
        {
            var autopilot = currentAutoPilot();
            if (autopilot == null || telemetry_field == null)
                return null;

            return telemetry_field.GetValue(autopilot) as TelemetryComponent;
        }

        public static void setAutoPilot(AutopilotMode mode)
        {
            var autopilot = currentAutoPilot();
            if (autopilot == null) return;
            if (autopilot.AutopilotMode == mode) return;
            autopilot.Enabled = true;
            autopilot.SetMode(mode);
        }
    }
}

