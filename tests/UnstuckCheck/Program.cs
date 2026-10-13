using System;
using MarioTest;

static class Program
{
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static void Main()
    {
        foreach (string status in new[] { "BackDown", "ForeDown", "ShortBackDown", "ShortForeDown",
            "SafeBackDown", "SafeForeDown", "CatchDown", "LandSafeDown", "JumpShortBackDown",
            "JumpShortForeDown", "JumpBackDown", "JumpForeDown", "Catch", "CatchStop", "CatchLost",
            "SlipFore", "SlipBack" })
            Check(MarioUnstuck.Fallen(status, true, 0f), "Missed prone state: " + status);
        foreach (string status in new[] { "Wait", "Run", "Squat", "HipAttackEnd" })
            Check(!MarioUnstuck.Fallen(status, true, 0f), "Standing state treated as fallen");
        foreach (string status in new[] { "DownLoser", "Hanging", "Ascend", "Descend" })
            Check(!MarioUnstuck.Fallen(status, false, -10f), "Death/ledge state treated as fallen");
        Check(MarioUnstuck.Fallen("Landing", false, -10f), "Blocked falling not detected");
        Check(!MarioUnstuck.Fallen("Jump", false, 10f), "Rising jump treated as fallen");
        Check(!MarioUnstuck.Fallen("Landing", false, 0f), "Steady hover treated as fallen");
        Console.WriteLine("PASS: fallen states");
    }
}
