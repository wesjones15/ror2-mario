using System;
using MarioTest;

static class Program
{
    static float Damage(float heights, int flags = 0) =>
        MarioFallDamage.Fraction(heights, flags, 40f, 80f, 0.0125f, 0.5f);

    static void Equal(float expected, float actual, string label)
    {
        if (Math.Abs(expected - actual) > 0.00001f || float.IsNaN(actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }

    static void Main()
    {
        Equal(0f, Damage(34f), "default rocket returns safely");
        Equal(0f, Damage(40f), "safe boundary");
        Equal(0.25f, Damage(60f), "long fall");
        Equal(0.9f, Damage(1000f), "extreme fall cap");
        Equal(0f, Damage(1000f, 2), "ground pound immunity");
        Equal(0f, Damage(1000f, 4), "soft landing immunity");
        Equal(0f, Damage(80f, 1), "dive full protection");
        Equal(0.125f, Damage(100f, 1), "higher dive partial protection");
        if (Damage(100f, 1) >= Damage(100f)) throw new Exception("diving must help");
        Equal(0f, Damage(float.NaN), "invalid height");
        Equal(0f, Damage(-10f), "negative height");
        Console.WriteLine("Fall damage policy checks passed.");
    }
}
