using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Numerics;
using MarioTest;
using Ror2Common;

static class Program
{
    static readonly Native.GroundFn ground = (IntPtr user, ref Native.Vec3 pos, out Native.GroundHit hit) =>
    { hit = new Native.GroundHit { normal = new Native.Vec3(0, 1, 0), flags = 64 }; return 1; };
    static Native.Host floor = new Native.Host { ground = Marshal.GetFunctionPointerForDelegate(ground) };
    static string assets;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    sealed class Rig : IDisposable
    {
        public IntPtr view, sim;
        public Rig(float y = 0)
        {
            var dir = NativeLib.CString(assets); var pos = new Native.Vec3(0, y, 0);
            view = Native.sms_view_new(dir, 0); sim = Native.sms_sim_new(dir, ref pos, 0);
            Check(view != IntPtr.Zero && sim != IntPtr.Zero, "native portrait couldn't load");
            Native.sms_sim_refill(sim);
        }
        public void Frame(float dt)
        {
            var frame = new Native.ViewFrame { dt = dt, forward = new Native.Vec3(0, 0, 1), up = new Native.Vec3(0, 1, 0) };
            Native.sms_view_update(view, sim, ref frame);
        }
        public void Step(Native.Pad pad, int ticks)
        { for (int i = 0; i < ticks; i++) { Native.sms_sim_step(sim, ref floor, ref pad, 0, 1f / 120f, view); Frame(1f / 120f); } }
        public void Dispose() { Native.sms_view_free(view); Native.sms_sim_free(sim); }
    }
    static void Main(string[] args)
    {
        assets = Path.GetFullPath(args[1]);
        if (!Directory.Exists(assets)) { Console.WriteLine("SKIP: native Mario disc cache absent"); return; }
        var handle = NativeLibrary.Load(Path.GetFullPath(args[0]));
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, search) => name == Native.Dll ? handle : IntPtr.Zero);
        Check(Native.Load(args[0], out var error), error);
        using (var r = new Rig())
        {
            r.Step(new Native.Pad { r = 1, r_analog = 255 }, 90);
            Check(Native.sms_view_water_info(r.view, out var water) != 0 && water.drop_quads > 0, "FLUDD has no water");
            var target = new Vector3(.18f, 1.5f, 0);
            var eye = target + new Vector3(-MathF.Sin(.95f) * MathF.Cos(.08f), MathF.Sin(.08f), MathF.Cos(.95f) * MathF.Cos(.08f)) * 1.05f;
            var forward = Vector3.Normalize(target - eye);
            var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
            var up = Vector3.Cross(forward, right);
            var frame = new Native.ViewFrame { eye = new Native.Vec3(eye.X * 100, eye.Y * 100, eye.Z * 100),
                forward = new Native.Vec3(forward.X, forward.Y, forward.Z), up = new Native.Vec3(up.X, up.Y, up.Z) };
            Native.sms_view_update(r.view, r.sim, ref frame);
            var ptr = Native.sms_view_water_vertices(r.view, out var count);
            int visible = 0;
            for (int i = 0; i < water.drop_quads * 4; i++)
            {
                var v = Marshal.PtrToStructure<Native.WaterVertex>(IntPtr.Add(ptr, i * Marshal.SizeOf<Native.WaterVertex>()));
                var p = new Vector3(v.pos.x, v.pos.y, v.pos.z) * .01f - eye;
                float z = Vector3.Dot(p, forward), half = z * MathF.Tan(.45f);
                if (z > .01f && Math.Abs(Vector3.Dot(p, right)) < half && Math.Abs(Vector3.Dot(p, up)) < half) visible++;
            }
            Check(visible > 0, "FLUDD water lies outside the actual portrait camera");
        }
        using (var r = new Rig())
        {
            r.Frame(0); Native.sms_view_nozzle_portrait(r.view);
            var draws = new ViewDraw[32]; uint count = Native.sms_view_draws(r.view, draws, 32);
            Check(count == 3 && draws.Take((int)count).Select(d => d.model).OrderBy(x => x).SequenceEqual(new uint[] { 8, 9, 10 }), "Air Pump must contain exactly three nozzle models");
            var pointer = Native.sms_view_positions(r.view, out var vertices);
            var xyz = new float[vertices * 3]; Marshal.Copy(pointer, xyz, 0, xyz.Length);
            Check(xyz.Length > 0 && xyz.All(float.IsFinite), "invalid nozzle vertices");
            float Width(int draw)
            {
                uint end = draw + 1 < count ? draws[draw + 1].first_vertex : vertices;
                var xs = Enumerable.Range((int)draws[draw].first_vertex, (int)(end - draws[draw].first_vertex)).Select(v => xyz[v * 3]);
                return xs.Max() - xs.Min();
            }
            Check(Width(0) > Math.Max(Width(1), Width(2)) * 1.5f,
                "wide Hover nozzle must be enlarged relative to Turbo and Rocket");
        }
        using (var r = new Rig(20000))
        {
            Native.sms_sim_portrait(r.sim, 1, 1, 22, 3, 1, 0); r.Step(default, 24);
            Check(Native.sms_sim_state(r.sim, out var state) != 0 && state.pounding == 2 && state.pos.y > 0, "pound must be an airborne super fall");
            Check(Native.sms_view_fx_info(r.view, out var fx) != 0 && fx.vertices > 0, "pound has no flames");
            var draws = new ViewDraw[32]; Native.sms_view_draws(r.view, draws, 32);
            float drawnY = draws[0].pos.y;
            Check(drawnY > state.pos.y + 1, "test must exercise falling interpolation, not stationary framing");
            r.Frame(0); Native.sms_view_draws(r.view, draws, 32);
            Check(Math.Abs(draws[0].pos.y - drawnY) < .001f, "camera-only reframe advanced the falling pose");
        }
        using (var r = new Rig())
        {
            var tuning = new Native.Tuning { jump_height = 1, move_speed = 1, spray_reach = 1, stomp_bounce = 1, rocket_height = 1, stomp_jump = 1, traction = 1, slide_fx_scale = 1.4f };
            Native.sms_sim_set_tuning(r.sim, ref tuning);
            var limits = new Native.Limits { wet_slide = 1, wet_slide_ready = 1, wet_slide_flags = 1, wet_slide_seconds = 2 };
            Native.sms_sim_set_limits(r.sim, ref limits);
            var velocity = new Native.Vec3(0, 0, 45); Native.sms_sim_launch(r.sim, ref velocity);
            Native.sms_sim_portrait(r.sim, 2, 2, 50, 1, 1, 1); r.Step(default, 96);
            Check(Native.sms_sim_state(r.sim, out var state) != 0 && state.pos.z > 500 && state.forward_vel > 1, "slide must develop in motion");
            var batches = Native.sms_view_fx_batches(r.view, out var count);
            bool ribbon = false;
            for (int i = 0; i < count; i++) { var b = Marshal.PtrToStructure<Native.FxBatch>(IntPtr.Add(batches, i * Marshal.SizeOf<Native.FxBatch>())); ribbon |= b.blend == 2 && b.vertex_count >= 8; }
            Check(ribbon, "moving slide has no native refraction ribbon");
        }
        Console.WriteLine("PASS: Mario ABI, spraying FLUDD, exactly three nozzle models, airborne pound flames and moving slide ribbon");
    }
}
