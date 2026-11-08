using System;
namespace MarioTest
{
    // Deliberately simple shapes. Rows are bottom-up for Unity; no game layout/art.
    internal static class BuiltinTankImage
    {
        internal const int Size = 96;
        internal static string Letter(byte nozzle) => nozzle == 1 ? "S" : nozzle == 5 ? "H" : nozzle == 2 ? "R" : nozzle == 6 ? "T" : "N";
        internal static byte[] Render(float water, float charge)
        {
            water = Math.Clamp(water, 0f, 1f); charge = Math.Clamp(charge, 0f, 1f);
            var pixels = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) {
                float dx = (x - 47.5f) / 47.5f, dy = (y - 47.5f) / 47.5f;
                float radius = (float)Math.Sqrt(dx * dx + dy * dy);
                if (radius > .94f) continue;
                byte r = 35, g = 40, b = 48;
                if (charge > 0f && dy <= -.88f + 1.76f * charge) { r = 255; g = 155; b = 25; }
                if (radius >= .88f) { r = 255; g = 225; b = 40; }
                if (Math.Abs(dx) < .33f && Math.Abs(dy) < .57f) {
                    r = g = b = 230;
                    if (Math.Abs(dx) < .27f && Math.Abs(dy) < .51f) {
                        bool wet = dy <= -.51f + 1.02f * water;
                        r = wet ? (byte)25 : (byte)15; g = wet ? (byte)145 : (byte)20; b = wet ? (byte)255 : (byte)27;
                    }
                }
                int p = (y * Size + x) * 4;
                pixels[p] = r; pixels[p+1] = g; pixels[p+2] = b; pixels[p+3] = 255;
            }
            return pixels;
        }
    }
}
