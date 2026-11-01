namespace MarioTest
{
    // Native select/equip slots are 0 none, then 1 Spray, 2 Hover, 3 Rocket,
    // 4 Turbo. State.nozzle instead exposes Sunshine's own nozzle kind IDs.
    internal enum MarioNozzleChoice
    {
        Spray = 0, Hover = 1, Rocket = 2, Turbo = 3, None = 4,
    }

    internal static class MarioNozzleChoices
    {
        public static uint NativeSlot(this MarioNozzleChoice choice) =>
            choice == MarioNozzleChoice.None ? 0u : (uint)choice + 1u;

        public static MarioNozzleChoice FromState(in Native.State state)
        {
            if (state.has_fludd == 0) return MarioNozzleChoice.None;
            switch (state.nozzle)
            {
                case 1: return MarioNozzleChoice.Spray;
                case 5: return MarioNozzleChoice.Hover;
                case 2: return MarioNozzleChoice.Rocket;
                case 6: return MarioNozzleChoice.Turbo;
                default: return MarioNozzleChoice.None;
            }
        }
    }
}
