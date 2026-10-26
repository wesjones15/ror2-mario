using System;

namespace MarioTest
{
    // The rider may hold a slope-safe pose, but the spent portal must keep its
    // own clock. Visibility changes sampling only; elapsed time never rewinds.
    internal struct MarioPortalPlayback
    {
        bool initialized;
        byte drawnState;
        public float Time { get; private set; }
        public byte State { get; private set; }

        public bool Sample(float elapsed, bool activated, bool handedOff, bool cut, float cutAt, bool visible)
        {
            State = (byte)(handedOff ? 2 : activated ? 1 : 0);
            Time = cut && !handedOff ? Math.Min(elapsed, cutAt) : elapsed;
            // Always process an exit and the first frame of a new state. In
            // particular, state 2 must retire the native rider/FX even offscreen.
            return State == 1 || visible || !initialized || drawnState != State;
        }

        public void Updated()
        {
            initialized = true;
            drawnState = State;
        }
    }
}
