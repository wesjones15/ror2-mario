namespace MarioTest
{
    // Which of libsms's Mario statuses are prone or blocked-in-a-fall, for ror2-common's Unstuck.
    // Status names follow libsms's MarioStatus mirror (spec: Player/MarioStatus.hpp).
    internal static class MarioUnstuck
    {
        internal static bool Fallen(string status, bool grounded, float verticalVelocity)
        {
            switch (status)
            {
                case "DownLoser": case "Hanging": case "Ascend": case "Descend":
                    return false;
                case "BackDown": case "ForeDown": case "ShortBackDown": case "ShortForeDown":
                case "SafeBackDown": case "SafeForeDown": case "CatchDown": case "LandSafeDown":
                case "JumpShortBackDown": case "JumpShortForeDown": case "JumpBackDown":
                case "JumpForeDown": case "Catch": case "CatchStop": case "CatchLost":
                case "SlipFore": case "SlipBack":
                    return true;
                default:
                    return !grounded && verticalVelocity < 0f;
            }
        }
    }
}
