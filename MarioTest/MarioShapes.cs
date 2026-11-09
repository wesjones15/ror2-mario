using System;
using Ror2Common;
using UnityEngine;
namespace MarioTest
{
    /// Asset-independent presentation of the existing Mario/water simulation.
    internal sealed class MarioShapes : IDisposable
    {
        // libsms's POSE_* bits: the native silhouette for hosts that draw a stand-in.
        const byte PoseDive = 1, PoseSlide = 2, PosePound = 4, PoseSpin = 8;
        const float Radius = 0.35f;
        const float CylinderHeight = 110f; // libsms::mario::STAND_IN_HEIGHT
        const float TankHalfLength = 0.4f;
        readonly PrimitiveShapes shapes = new PrimitiveShapes("Mario built-in shapes");
        readonly Native.WaterSegment[] water = new Native.WaterSegment[512];
        readonly Native.WaterVertex[] wet = new Native.WaterVertex[16384];
        SurfaceQuads ground;
        Native.State state;
        public void Draw(IntPtr sim, Vector3 feet, Quaternion facing, uint skin, float lag = 0f)
        {
            Native.sms_sim_state(sim, out state);
            shapes.Begin();
            float height = CylinderHeight * UnityHost.Scale;
            var bodyRotation = facing * Quaternion.AngleAxis(state.flip_pitch * (180f / 32768f), Vector3.right);
            var bodyFeet = feet;
            ViewDraw root = default;
            bool corkscrew = (state.pose & PoseDive) != 0 && Native.sms_sim_corkscrew_pose(sim, out root) != 0;
            if (corkscrew)
            {
                bodyRotation = new Quaternion(root.rot_x, root.rot_y, root.rot_z, root.rot_w);
                // Body() centers the cylinder half its height above its supplied feet.
                bodyFeet += new Vector3(root.pos.x, root.pos.y, root.pos.z) * UnityHost.Scale
                    + (bodyRotation * Vector3.up - Vector3.up) * (height * 0.5f);
            }
            Pose(bodyFeet, bodyRotation, MarioSkin.StandInColor(skin), height, corkscrew);
            if (state.has_fludd != 0 && Native.sms_sim_stand_in_tank_pose(sim, out var mount) != 0)
            {
                var pack = feet + new Vector3(mount.pos.x, mount.pos.y, mount.pos.z) * UnityHost.Scale;
                var rotation = new Quaternion(mount.rot_x, mount.rot_y, mount.rot_z, mount.rot_w);
                shapes.Show(PrimitiveType.Cylinder, pack, new Vector3(.35f, TankHalfLength, .35f), rotation, Color.yellow);
                var neck = pack + rotation * Vector3.up * TankHalfLength;
                var drawOffset = feet - UnityHost.ToUnity(state.pos);
                uint countNozzles = state.nozzle == 3 || state.nozzle == 5 ? 2u : 1u;
                for (uint nozzle = 0; nozzle < countNozzles; nozzle++)
                {
                    if (Native.sms_sim_emitter(sim, nozzle, out var origin, out var aim) == 0) continue;
                    var at = UnityHost.ToUnity(origin) + drawOffset;
                    // One short blue head: its tip is the outlet and its axis is the shared emitter
                    // direction (the one the water leaves along), so it floats above the tank when
                    // the aim says so, in the standing and wet-slide poses alike.
                    var dir = new Vector3(aim.x, aim.y, aim.z);
                    float head = state.nozzle == 4 ? .18f : .3f;
                    if (dir.sqrMagnitude > 1e-6f) shapes.Segment(at - dir.normalized * head, at, .07f, Color.blue);
                    else shapes.Segment(neck, at, .07f, Color.blue);
                }
            }
            int count = (int)Math.Min((uint)water.Length, Native.sms_sim_water_segments(sim, lag, water, (uint)water.Length));
            for (int i = 0; i < count; i++)
            {
                var p = water[i];
                // Keep the thin stream's existing silhouette; allow a light burst's
                // own simulated size instead of flattening every drop to the same cap.
                float radius = p.radius * UnityHost.Scale;
                if ((p.flags & 1) == 0) radius = Mathf.Clamp(radius / .707f, .025f, .15f);
                var tail = UnityHost.ToUnity(p.tail);
                var head = Vector3.Lerp(tail, UnityHost.ToUnity(p.head), 0.5f);
                shapes.Segment(head, tail, Mathf.Max(.025f, radius), Color.cyan);
            }
            shapes.End();
            if(ground==null)ground=new SurfaceQuads("Mario fallback wet ground",new Color(0,0,0,Mathf.Clamp01(Plugin.GroundWetness.Value)));
            int vertices=(int)Math.Min((uint)wet.Length,Native.sms_sim_wet_vertices(sim,wet,(uint)wet.Length));
            ground.Begin();for(int i=0;i<vertices;i++)ground.Vertex(UnityHost.ToUnity(wet[i].pos),new Vector2(wet[i].u,wet[i].v),wet[i].alpha);ground.End();
        }
        /// Follows the native silhouettes: horizontal along a dive or belly slide, squashed in
        /// a ground pound, and a wider core around a spin jump or spinning pound.
        void Pose(Vector3 feet, Quaternion facing, Color color, float height, bool corkscrew)
        {
            if ((state.pose & PosePound) != 0) height *= 0.55f;
            if (!corkscrew && (state.pose & (PoseDive | PoseSlide)) != 0)
            {
                bool slide = (state.pose & PoseSlide) != 0;
                var rotation = facing * Quaternion.Euler(90f, 0f, 0f);
                var center = feet + Vector3.up * (Radius * (slide ? 0.75f : 0.95f))
                    + facing * Vector3.forward * (height * 0.25f);
                shapes.Show(PrimitiveType.Cylinder, center,
                    new Vector3(Radius * 2f, height * 0.5f, Radius * 2f), rotation, color);
                shapes.Show(PrimitiveType.Sphere,
                    center + facing * Vector3.forward * (height * 0.4f) + Vector3.up * (Radius * 0.3f),
                    Vector3.one * (Radius * 0.35f), facing, Color.white);
            }
            else
            {
                shapes.Body(feet, facing, color, height, Radius);
            }
            if (corkscrew || (state.pose & PoseSpin) != 0)
            {
                // A cylinder body can't show its own rotation; this wide core does.
                shapes.Show(PrimitiveType.Cylinder, feet + Vector3.up * (height * 0.5f),
                    new Vector3(Radius * 4f, height * 0.2f, Radius * 4f), facing,
                    Color.Lerp(color, Color.white, 0.35f));
            }
        }

        public void Hide() {shapes.Hide();ground?.Hide();}
        public void Dispose() {shapes.Dispose();ground?.Dispose();}
    }
}
