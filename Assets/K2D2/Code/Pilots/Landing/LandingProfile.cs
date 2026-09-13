using System;
using KSP.Sim.impl;

namespace K2D2.Landing
{
    // Single shared switch for Landing's Atmo/Vacuum profile split (see LandingSettings.cs/
    // TouchDown.cs) - one source of truth every "which profile is active right now" read goes
    // through, kept up to date by LandingPilot.Update() calling Update() below once per tick
    // wherever it already has the current vessel/body on hand.
    //
    // IsAtmospheric only actually needs to be current for internal C# reads (LandingPilot,
    // TouchDown, DeorbitBurn, MidCourseCorrection all just read the property fresh every call - no
    // staleness possible there). Changed exists purely for the one place staleness DOES matter -
    // LandingUI's two profile panels - so it can show/hide the right one without polling every
    // single frame (though it happens to get checked every tick anyway, same as the existing orbit
    // gate, so this is mostly a convenience rather than a hard requirement).
    public static class LandingProfile
    {
        public static bool IsAtmospheric { get; private set; } = false;

        public static event Action Changed;

        public static void Update(CelestialBodyComponent body)
        {
            bool atmo = body != null && body.hasAtmosphere;
            if (atmo == IsAtmospheric)
                return;

            IsAtmospheric = atmo;
            Changed?.Invoke();
        }
    }
}
