using System;
using Ror2Common;
using UnityEngine;
namespace MarioTest
{
    /// Asset-independent presentation of the existing Mario/water simulation.
    internal sealed class MarioShapes : IDisposable
    {
        readonly PrimitiveShapes shapes = new PrimitiveShapes("Mario built-in shapes");
        readonly Native.WaterParticle[] water = new Native.WaterParticle[512];
        Native.State state;
        public void Draw(IntPtr sim, Vector3 feet, Quaternion facing)
        {
            Native.sms_sim_state(sim, out state);
            shapes.Begin();
            shapes.Body(feet, facing, Color.red, UnityHost.MarioHeight * UnityHost.Scale);
            int count = (int)Math.Min((uint)water.Length, Native.sms_sim_water_particles(sim, water, (uint)water.Length));
            for (int i = 0; i < count; i++)
            {
                var p = water[i];
                var at = UnityHost.ToUnity(p.pos);
                var velocity = new Vector3(p.velocity.x, p.velocity.y, p.velocity.z) * UnityHost.Scale;
                shapes.Segment(at, at - velocity * 0.3f, Mathf.Clamp(p.radius * UnityHost.Scale, 0.025f, 0.15f), Color.cyan);
            }
            shapes.End();
        }
        public void Hud()
        {
            string nozzle = state.nozzle == 0 ? "Off" : state.nozzle == 1 ? "Spray" : state.nozzle == 2 ? "Rocket"
                : state.nozzle == 5 ? "Hover" : state.nozzle == 6 ? "Turbo" : "FLUDD";
            GUI.Label(new Rect(24, Screen.height - 110, 300, 30), $"FLUDD: {nozzle}   Water: {state.water:P0}");
        }
        public void Hide() => shapes.Hide();
        public void Dispose() => shapes.Dispose();
    }
}
