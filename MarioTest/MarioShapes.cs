using System;
using Ror2Common;
using UnityEngine;
namespace MarioTest
{
    /// Asset-independent presentation of the existing Mario/water simulation.
    internal sealed class MarioShapes : IDisposable
    {
        // libsms's POSE_* bits: the native silhouette for hosts that draw a stand-in.
        const byte PoseDive = 1, PoseSlide = 2, PosePound = 4, PoseSpin = 8, PoseDown = 16, PoseFaceDown = 32, PoseFire = 64;
        const float Radius = 0.35f;
        const float CylinderHeight = 110f; // libsms::mario::STAND_IN_HEIGHT
        const float TankHalfLength = 0.4f;
        readonly PrimitiveShapes shapes = new PrimitiveShapes("Mario built-in shapes");
        readonly LemurianFlames flames = new LemurianFlames();
        readonly Native.WaterSegment[] water = new Native.WaterSegment[512];
        readonly Native.WaterVertex[] wet = new Native.WaterVertex[16384];
        SurfaceQuads ground;
        Native.State state;
        // Stand-in Rocket smoke: a short trail of fading white puffs from the resolved outlet along its exhaust
        // direction, once per launch (the rising edge of a full Rocket charge), upright or level alike.
        struct Puff { public Vector3 at, dir; public float born; }
        const float PuffLife = 0.9f;
        readonly System.Collections.Generic.List<Puff> puffs = new System.Collections.Generic.List<Puff>();
        public void Draw(IntPtr sim, Vector3 feet, Quaternion facing, uint skin, float lag = 0f, bool rocketLaunch = false)
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
            // On fire (a super pound, a burning corkscrew or slide, a flaming Rocket spin): the Lemurian's fireball burns on him.
            flames.Set((state.pose & PoseFire) != 0, bodyFeet + Vector3.up * (height * 0.5f), height);
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
            RocketSmoke(sim, feet, rocketLaunch);
            int count = (int)Math.Min((uint)water.Length, Native.sms_sim_water_segments(sim, lag, water, (uint)water.Length));
            for (int i = 0; i < count; i++)
            {
                var p = water[i];
                // Keep the thin stream's existing silhouette; allow a light burst's
                // own simulated size instead of flattening every drop to the same cap.
                float radius = p.radius * UnityHost.Scale;
                if ((p.flags & 1) == 0) radius = Mathf.Clamp(radius / .707f, .025f, .15f);
                var tail = UnityHost.ToUnity(p.tail);
                // The Rocket's precision slug (flag 4) is a bright tracer: its whole streak, thick enough to see at its speed.
                if ((p.flags & 4) != 0)
                {
                    shapes.Segment(UnityHost.ToUnity(p.head), tail, Mathf.Max(.09f, radius), Color.white);
                    continue;
                }
                var head = Vector3.Lerp(tail, UnityHost.ToUnity(p.head), 0.5f);
                shapes.Segment(head, tail, Mathf.Max(.025f, radius), Color.cyan);
            }
            shapes.End();
            if(ground==null)ground=new SurfaceQuads("Mario fallback wet ground",new Color(0,0,0,Mathf.Clamp01(Plugin.GroundWetness.Value)));
            int vertices=(int)Math.Min((uint)wet.Length,Native.sms_sim_wet_vertices(sim,wet,(uint)wet.Length));
            ground.Begin();for(int i=0;i<vertices;i++)ground.Vertex(UnityHost.ToUnity(wet[i].pos),new Vector2(wet[i].u,wet[i].v),wet[i].alpha);ground.End();
        }
        void RocketSmoke(IntPtr sim, Vector3 feet, bool launched)
        {
            // The launch comes from the sim's TriggerFired event (a gauge sample can miss its few ticks).
            if (launched && Native.sms_sim_emitter(sim, 0, out var origin, out var aim) != 0)
            {
                var at = UnityHost.ToUnity(origin) + (feet - UnityHost.ToUnity(state.pos));
                var dir = new Vector3(aim.x, aim.y, aim.z);
                dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.down;
                var side = Vector3.Cross(dir, Vector3.up);
                if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
                side.Normalize();
                // A fat cloud that stays where the launch was (a rising Mario leaves it behind as a trail).
                for (int i = 0; i < 14; i++)
                {
                    float spread = Mathf.Sin(i * 2.399f) * 0.35f;
                    puffs.Add(new Puff { at = at + side * spread + Vector3.up * (Mathf.Cos(i * 2.399f) * 0.2f), dir = dir, born = Time.time + i * 0.02f });
                }
                Plugin.Log.LogDebug("Stand-in Rocket smoke started.");
            }
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                var p = puffs[i];
                float age = Time.time - p.born;
                if (age > PuffLife) { puffs.RemoveAt(i); continue; }
                if (age < 0f) continue;
                float t = age / PuffLife;
                // Fades by greying and shrinking at the end (the shape material is opaque); five steps keep the materials few.
                float grey = Mathf.Round(t * 5f) / 5f;
                var where = p.at + p.dir * (age * 1.2f) + Vector3.up * (1.0f * t);
                float size = Mathf.Lerp(0.55f, 1.3f, Mathf.Min(1f, t * 2f)) * (t > 0.6f ? 1f - (t - 0.6f) * 2f : 1f);
                shapes.Show(PrimitiveType.Sphere, where, Vector3.one * Mathf.Max(0.05f, size),
                    Quaternion.identity, Color.Lerp(Color.white, new Color(0.6f, 0.6f, 0.64f), grey));
            }
        }
        /// Follows the native silhouettes: horizontal along a dive or belly slide, squashed in
        /// a ground pound, and a wider core around a spin jump or spinning pound.
        void Pose(Vector3 feet, Quaternion facing, Color color, float height, bool corkscrew)
        {
            if ((state.pose & PosePound) != 0) height *= 0.55f;
            if (!corkscrew && (state.pose & (PoseDown | PoseFaceDown)) != 0)
            {
                // Knocked down: flat on the ground, on his back (head behind him, face up) or on his front
                // (head ahead), his feet where they were.
                bool onBack = (state.pose & PoseDown) != 0;
                var toHead = facing * Vector3.forward * (onBack ? -1f : 1f);
                var rotation = facing * Quaternion.Euler(onBack ? -90f : 90f, 0f, 0f);
                var center = feet + Vector3.up * (Radius * 0.9f) + toHead * (height * 0.45f);
                shapes.Show(PrimitiveType.Cylinder, center,
                    new Vector3(Radius * 2f, height * 0.5f, Radius * 2f), rotation, color);
                // The face looks up when he is on his back; on his front it is in the ground.
                if (onBack)
                    shapes.Show(PrimitiveType.Sphere, center + toHead * (height * 0.38f) + Vector3.up * (Radius * 0.85f),
                        Vector3.one * (Radius * 0.35f), facing, Color.white);
            }
            else if (!corkscrew && (state.pose & (PoseDive | PoseSlide)) != 0)
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

        public void Hide() {shapes.Hide();ground?.Hide();flames.Set(false, default, 0f);}
        public void Dispose() {shapes.Dispose();ground?.Dispose();flames.Set(false, default, 0f);}
    }
}
