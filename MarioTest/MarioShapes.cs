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
        readonly PrimitiveShapes shapes = new PrimitiveShapes("Mario built-in shapes");
        readonly Native.WaterSegment[] water = new Native.WaterSegment[512];
        readonly Native.WaterVertex[] wet = new Native.WaterVertex[16384];
        SurfaceQuads ground;
        Native.State state;
        public void Draw(IntPtr sim, Vector3 feet, Quaternion facing, uint skin, float lag = 0f)
        {
            Native.sms_sim_state(sim, out state);
            shapes.Begin();
            Pose(feet, facing, MarioSkin.StandInColor(skin), UnityHost.MarioHeight * UnityHost.Scale);
            if(state.has_fludd!=0){
                bool flat=(state.pose&(PoseDive|PoseSlide))!=0;
                var pack=flat ? feet+Vector3.up*.65f+facing*Vector3.forward*.4f : feet+Vector3.up*.85f-facing*Vector3.forward*.35f;
                shapes.Show(PrimitiveType.Cylinder,pack,new Vector3(.35f,.4f,.35f),flat?facing*Quaternion.Euler(90,0,0):facing,Color.yellow);
                for(uint nozzle=0;nozzle<(state.nozzle==5?2u:1u);nozzle++)if(Native.sms_sim_emitter(sim,nozzle,out var origin,out var aim)!=0){
                    var at=UnityHost.ToUnity(origin);var dir=new Vector3(aim.x,aim.y,aim.z);
                    shapes.Segment(at,at-dir*.18f,.07f,Color.blue);}
            }
            int count = (int)Math.Min((uint)water.Length, Native.sms_sim_water_segments(sim, lag, water, (uint)water.Length));
            for (int i = 0; i < count; i++)
            {
                var p = water[i];
                // Keep the thin stream's existing silhouette; allow a light burst's
                // own simulated size instead of flattening every drop to the same cap.
                float radius = p.radius * UnityHost.Scale;
                if ((p.flags & 1) == 0) radius = Mathf.Clamp(radius / .707f, .025f, .15f);
                shapes.Segment(UnityHost.ToUnity(p.head), UnityHost.ToUnity(p.tail), Mathf.Max(.025f, radius), Color.cyan);
            }
            shapes.End();
            if(ground==null)ground=new SurfaceQuads("Mario fallback wet ground",new Color(0,0,0,Mathf.Clamp01(Plugin.GroundWetness.Value)));
            int vertices=(int)Math.Min((uint)wet.Length,Native.sms_sim_wet_vertices(sim,wet,(uint)wet.Length));
            ground.Begin();for(int i=0;i<vertices;i++)ground.Vertex(UnityHost.ToUnity(wet[i].pos),new Vector2(wet[i].u,wet[i].v),wet[i].alpha);ground.End();
        }
        /// Follows the native silhouettes: horizontal along a dive or belly slide, squashed in
        /// a ground pound, and a wider core around a spin jump or spinning pound.
        void Pose(Vector3 feet, Quaternion facing, Color color, float height)
        {
            if ((state.pose & PosePound) != 0) height *= 0.55f;
            if ((state.pose & (PoseDive | PoseSlide)) != 0)
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
            if ((state.pose & PoseSpin) != 0)
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
