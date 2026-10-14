using KSP.Sim.impl;

namespace K2D2.Landing
{
    // Which of Landing's two profiles (Atmo or Vacuum, see LandingSettings) applies: the current
    // body has an atmosphere or not. LandingPilot.Update() keeps it current every tick.
    public static class LandingProfile
    {
        public static bool IsAtmospheric { get; private set; } = false;

        public static void Update(CelestialBodyComponent body)
        {
            IsAtmospheric = body != null && body.hasAtmosphere;
        }
    }
}
