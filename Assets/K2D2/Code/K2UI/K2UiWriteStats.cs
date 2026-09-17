using UnityEngine;

// CS0162 = unreachable code. It fires three times below BY DESIGN: with Enabled const-false the
// compiler folds every `if (Enabled)` branch away, which is precisely the production behaviour we
// want (the probe must cost literally nothing when off) - but the folded-away increment statements
// are then flagged. Disabling it here rather than weakening `const` to `static readonly` keeps the
// compile-time elimination, so the probe's zero cost is enforced by the compiler instead of relying
// on the JIT to notice a field read. If these warnings ever DISAPPEAR, the probe is no longer being
// compiled out - that is the signal something changed.
#pragma warning disable CS0162

namespace K2UI
{
    // The measured write-throughput probe. Ported from FlightPlan's FpUiController.TickUiWriteSummary
    // so the same reading applies in both mods and across the launch matrices.
    public static class K2UiWriteStats
    {
        // PRODUCTION (v1.2.1): OFF. The probe is a MEASUREMENT INSTRUMENT, not a feature - it exists to
        // prove the change-detection win and it did that job (19 windows measured 99.93% of UI writes
        // skipped; see Deploy/obj/uifix-launch-4-closeout.md). Shipping it would mean a log line every
        // 5 s forever, which is exactly the chatter this release removes. One constant turns it back
        // on for a future perf investigation, and the counters below cost nothing while it is off.
        public const bool Enabled = false;            // one constant turns the whole probe off
        public const float SummaryInterval = 5.0f;    // one constant controls the interval

        public static long performed;                 // writes actually executed
        public static long skipped;                   // writes suppressed by change detection
        static float _nextSummaryAt;

        public static void Performed()
        {
            if (Enabled)
                performed++;
        }

        public static void Skipped()
        {
            if (Enabled)
                skipped++;
        }

        // Call on the hidden -> visible transition (F2) and on invalidation.
        public static void ReArm()
        {
            performed = 0;
            skipped = 0;
            _nextSummaryAt = 0f;
        }

        // Call ONCE per frame, only while the window is open.
        public static void Tick()
        {
            // Gated on the constant as a call rather than as an early `if (!Enabled) return;` -
            // with Enabled const-true the early return makes the rest of the method unreachable and
            // the compiler says so (CS0162). False here still removes the whole probe at compile time.
            if (Enabled)
                TickEnabled();
        }

        static void TickEnabled()
        {
            float now = Time.unscaledTime;
            if (_nextSummaryAt <= 0f)
            {
                _nextSummaryAt = now + SummaryInterval;
                return;
            }
            if (now < _nextSummaryAt)
                return;
            _nextSummaryAt = now + SummaryInterval;

            // The null-logger guard that used to sit here is gone, and so is its _loggedNullLogger flag.
            // It was retained from the pre-shim version of this file, where the summary went straight to
            // ILogger and a null logger had to be checked by hand; the L.* shim checks for a null logger
            // itself (that is what F5(a) built it for), so fetching the logger just to null-test it was
            // dead weight - and its "log this once" branch had already been emptied, leaving a flag that
            // set itself and reported nothing.

            // Deliberately L.Info, not L.Log: if the probe is switched back on, its summary is a
            // MEASUREMENT a developer must be able to see in Ksp2.log, so it belongs on the one level
            // that is actually visible. (While Enabled is false this is unreachable - Tick() returns
            // early - so it is not a second production log line in a shipped build.)
            K2D2.L.Info($"UI write summary: performed = {performed}, skipped = {skipped} (last 5.0 s)");

            performed = 0;
            skipped = 0;
        }
    }
}

#pragma warning restore CS0162
