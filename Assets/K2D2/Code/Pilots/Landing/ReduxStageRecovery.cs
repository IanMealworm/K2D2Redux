using System;
using System.Reflection;

namespace K2D2.Landing
{
    // What Redux's stage recovery is doing (Redux.StageRecovery.StageRecoveryManager in
    // Ksp2Redux, all internal - so read through reflection, like ReduxWaypoints).
    //
    // When stages separate, Redux saves a snapshot of the dropped stage if it has a probe core and
    // can be controlled. Replaying one saves the mission, loads that snapshot at the moment of
    // separation and hands the player the stage as the active vessel ("FlyingStage"); after it
    // lands Redux records the result and loads the mission back. K2D2 only reads this - for the
    // boost-back landing's status line and logs.
    public struct ReduxReplay
    {
        public bool available;
        // Redux's StageRecoverySessionPhase: Idle, SavingMission, LoadingStage, FlyingStage,
        // LoadingMission.
        public string phase;
        public bool flying_stage => phase == "FlyingStage";
        public string vessel_name;
        public double captured_UT;
    }

    public static class ReduxStageRecovery
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

        static PropertyInfo instance, session_phase, active_record;
        static FieldInfo vessel_name, captured_UT;
        static bool? _resolved;

        static bool Resolve()
        {
            try
            {
                var manager = Type.GetType("Redux.StageRecovery.StageRecoveryManager, Ksp2Redux");
                instance = manager?.GetProperty("Instance", Any | BindingFlags.Static);
                session_phase = manager?.GetProperty("SessionPhase", Any | BindingFlags.Instance);
                active_record = manager?.GetProperty("ActiveRecord", Any | BindingFlags.Instance);
                var record = active_record?.PropertyType;
                vessel_name = record?.GetField("VesselName", Any | BindingFlags.Instance);
                captured_UT = record?.GetField("CapturedUniversalTime", Any | BindingFlags.Instance);
                return instance != null && session_phase != null && active_record != null && vessel_name != null && captured_UT != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static ReduxReplay Get()
        {
            var replay = new ReduxReplay { phase = "Unavailable" };
            if (!(_resolved ??= Resolve()))
                return replay;

            object manager = instance.GetValue(null);
            if (manager == null)
                return replay;

            replay.available = true;
            replay.phase = session_phase.GetValue(manager)?.ToString() ?? "Unknown";
            object record = active_record.GetValue(manager);
            if (record != null)
            {
                replay.vessel_name = vessel_name.GetValue(record) as string;
                replay.captured_UT = (double)captured_UT.GetValue(record);
            }
            return replay;
        }
    }
}
