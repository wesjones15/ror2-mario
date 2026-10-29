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
        using (var fire = new Rig())
        {
            Check(Native.sms_sim_ignite(fire.sim) != 0, "fire state failed to start");
            fire.Step(new Native.Pad(), 36);
            Native.sms_view_fx_vertices(fire.view, out uint corners);
            Check(corners > 0, "emitter 6 fire damage was not drawn through the C ABI");
            var a = new Native.Vec3(0, 100, 0);
            var b = new Native.Vec3(0, 100, 500);
            Check(Native.sms_sim_water_touches_segment(fire.sim, ref a, ref b, 1000) == 0, "fire alone manufactured water contacts");
            Native.sms_sim_wet(fire.sim);
            Check(!Marshal.PtrToStringAnsi(Native.sms_sim_status_name(fire.sim)).StartsWith("Fire"), "body water retained a fire phase");
            Check(Native.sms_sim_ignite(fire.sim) == 0, "body water allowed immediate re-ignition");
            fire.Step(new Native.Pad(), 40);
            Check(Native.sms_view_water_info(fire.view, out var runoff) != 0 && runoff.drop_quads > 0,
                "vanilla WetWaterEmit droplets did not reach the native water renderer");
        }
        using (var hop = new Rig())
        {
            Check(Native.sms_sim_ignite(hop.sim) != 0, "burn hop did not start");
            Native.sms_sim_state(hop.sim, out var launch);
            Check(Math.Abs(launch.vel.y - 50f) < 0.01f, "burn hop must use disc mFireDownForce=50");
            bool recovery = false;
            for (int tick = 0; tick < 360; tick++)
            {
                hop.Step(new Native.Pad(), 1);
                string status = Marshal.PtrToStringAnsi(Native.sms_sim_status_name(hop.sim));
                Check(status != "FireDash", "native fire entered a panic run");
                recovery |= status == "FireJumpEnd";
            }
            Check(recovery, "native burn hop skipped vanilla recovery");
        }
        using (var water = new Rig())
        {
            water.Step(new Native.Pad { r_analog = 255 }, 60);
            var a = new Native.Vec3(0, 100, 0);
            var b = new Native.Vec3(0, 100, 500);
            Check(Native.sms_sim_water_touches_segment(water.sim, ref a, ref b, 1000) != 0, "spray did not expose real water contacts");
            var far = new Native.Vec3(100000, 100000, 100000);
            Check(Native.sms_sim_water_touches_segment(water.sim, ref far, ref far, 1) == 0, "distant fire extinguished");
            Native.sms_sim_teleport(water.sim, ref far);
            Check(Native.sms_sim_water_touches_segment(water.sim, ref a, ref b, 1000) == 0, "teleport retained stale water contacts");
        }
        Console.WriteLine("PASS: native burn hop/recovery, wet runoff, fire flare, real FLUDD contacts and cleanup");
        if (File.Exists(Path.Combine(assets, "heat_haze", "shimmerlow.bmd")))
        {
            var heat = Native.sms_heat_new(NativeLib.CString(assets));
            Check(heat != IntPtr.Zero, "ShimmerLow native handle");
            try
            {
                var frame = new float[8];
                Check(Native.sms_heat_frame(heat, 0f, frame, 8) != 0 && frame[0] == 2425f && frame[1] > 1f, "heat-haze depth/gain marshaling");
                float scroll = frame[7];
                Check(Native.sms_heat_frame(heat, 0.25f, frame, 8) != 0 && Math.Abs(frame[7] - scroll) > 0.1f, "heat-haze BTK scroll");
                var shortPixels = new byte[] { 171, 171, 171, 171 };
                Check(Native.sms_heat_texture(heat, out var texture, shortPixels, 4) == 0 && shortPixels[0] == 171 && shortPixels[3] == 171, "heat-haze short buffer rejected");
                var pixels = new byte[64 * 64 * 4];
                Check(Native.sms_heat_texture(heat, out texture, pixels, (uint)pixels.Length) != 0 && texture.width == 64 && pixels[0] == 255, "heat-haze normal map marshaling");
            }
            finally { Native.sms_heat_free(heat); }
            Console.WriteLine("PASS: ShimmerLow depth, compensated normal gain, animated UVs and managed/native buffer safety");
        }
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
        CheckEntrance();
        CheckFastEntrance(false);
        CheckFastEntrance(true);
        CheckPortalPlayback();
        CheckSameRigSkin();
        CheckYoshiSkin();
        Console.WriteLine("PASS: Mario ABI, FLUDD, nozzle models, pound, slide, skins and stage-one statue entrance handoff");
    }

    // A same-rig alt-skin (Piantissimo, id 2) loads through the generic skin call and draws its own
    // body instead of Mario's, with Mario's FLUDD and no separate cap or hands.
    static void CheckSameRigSkin()
    {
        if (!File.Exists(Path.Combine(assets, "npc", "monteman", "monteman_model.bmd")))
        { Console.WriteLine("SKIP: Piantissimo's model absent (extras not extracted)"); return; }
        using var r = new Rig();
        Check(Native.sms_view_load_skin(r.view, 2, null) != 0, "Piantissimo's model should load from the Sunshine assets");
        Check(Native.sms_view_load_skin(r.view, 2, null) != 0, "loading a skin twice must stay true and not add a second body");
        Native.sms_view_skin(r.view, 2);
        r.Step(new Native.Pad { stick_y = 1f }, 60);
        var draws = new ViewDraw[32]; uint n = Native.sms_view_draws(r.view, draws, 32);
        uint models = Native.sms_view_model_count(r.view);
        var drawn = draws.Take((int)n).Select(d => d.model).ToArray();
        Check(!drawn.Contains(0u), "Mario's own body drew under the Piantissimo skin");
        Check(drawn.Any(m => m >= 13 && m < models), "no Piantissimo body in the draw list: " + string.Join(",", drawn));
        Check(drawn.Contains(6u), "FLUDD should stay Mario's under a same-rig skin");
        Check(!drawn.Contains(5u), "his cap is part of his model");
        Check(draws.Take((int)n).Select(d => d.key).Distinct().Count() == n, "instance keys collide");
        Native.sms_view_skin(r.view, 0); r.Frame(0);
        n = Native.sms_view_draws(r.view, draws, 32);
        Check(draws.Take((int)n).Any(d => d.model == 0), "switching back must draw Mario's body");
        Check(Native.sms_view_load_skin(r.view, 99, null) == 1, "an unknown id is Mario, which is always loaded");
    }

    // Yoshi (id 3) is a different rig: his own model joins the table after the nozzles, and the
    // draw list swaps Mario's body, cap and hands for it while FLUDD stays.
    static void CheckYoshiSkin()
    {
        if (!File.Exists(Path.Combine(assets, "yoshi", "yoshi_model.bmd")))
        { Console.WriteLine("SKIP: Yoshi's model absent"); return; }
        using var r = new Rig();
        uint before = Native.sms_view_model_count(r.view);
        Check(Native.sms_view_load_skin(r.view, 3, null) != 0, "Yoshi's model should load from the Sunshine assets");
        Check(Native.sms_view_load_skin(r.view, 3, null) != 0, "loading Yoshi twice must stay true and not add a second body");
        Check(Native.sms_view_model_count(r.view) == before + 1, "Yoshi should add exactly one model");
        Native.sms_view_skin(r.view, 3);
        r.Step(new Native.Pad { stick_y = 1f }, 60);
        var draws = new ViewDraw[32]; uint n = Native.sms_view_draws(r.view, draws, 32);
        uint models = Native.sms_view_model_count(r.view);
        var drawn = draws.Take((int)n).Select(d => d.model).ToArray();
        Check(!drawn.Contains(0u), "Mario's own body drew under the Yoshi skin");
        Check(drawn.Any(m => m >= 12 && m < models), "no Yoshi body in the draw list: " + string.Join(",", drawn));
        Check(drawn.Contains(6u), "FLUDD should stay Mario's under the Yoshi skin");
        Check(!drawn.Contains(5u), "Yoshi carries no cap of Mario's");
        // A front Spray leaves the mouth, not FLUDD's nozzle (the mouth joint is ~116 up).
        Native.sms_sim_set_yoshi_skin(r.sim, 1);
        r.Step(new Native.Pad { r_analog = 255 }, 30);
        Check(Native.sms_sim_last_spray(r.sim, out var spray) != 0, "no juice emit under the Yoshi skin");
        Check(spray.pos.y > 80f, $"the juice left at y={spray.pos.y}, not the mouth");
        Native.sms_view_skin(r.view, 0); r.Frame(0);
        n = Native.sms_view_draws(r.view, draws, 32);
        Check(draws.Take((int)n).Any(d => d.model == 0), "switching back must draw Mario's body");
    }

    static void CheckEntrance()
    {
        if (!File.Exists(Path.Combine(assets, "portal", "05_gate01.bmd"))) { Console.WriteLine("SKIP: statue portal cache absent"); return; }
        var view = Native.sms_view_entrance_new(NativeLib.CString(assets), 0);
        Check(view != IntPtr.Zero, "native statue entrance couldn't load");
        try {
            Check(Native.sms_view_entrance_bounds(view, out var lo, out var hi) != 0, "no pedestal collision bounds");
            Check(lo.y >= -.1f && hi.y > 300 && hi.z < 308.53f - 70, "pedestal blocks Mario's landing point");
            var f = new Native.ViewFrame { forward = new Native.Vec3(0, 0, 1), up = new Native.Vec3(0, 1, 0) };
            var draws = new ViewDraw[32];
            Check(Native.sms_view_entrance_update(view, .5f, 0, ref f, out _) == 1, "idle entrance failed");
            var n = Native.sms_view_draws(view, draws, 32);
            Check(n == 2 && draws.Take((int)n).All(d => d.kind >= 240), "Mario visible before X");
            var gate = draws.Take((int)n).Single(d => d.kind == 241);
            Native.sms_view_model(view, gate.model, out var info);
            var uv = new float[info.vertex_count * 2]; Native.sms_view_uvs(view, gate.model, uv, info.vertex_count);
            int texture = -1;
            var batches = new ViewBatch[info.batch_count]; Native.sms_view_batches(view, gate.model, batches, info.batch_count);
            texture = batches[0].texture;
            var version = Native.sms_view_texture_version(view, (uint)texture);
            bool particles = false, gotCapInHand = false, done = false;
            for (int i = 0; i <= 180; i++) {
                float time = i / 30f;
                var result = Native.sms_view_entrance_update(view, time, 1, ref f, out var feet);
                Check(result != 0, "entrance update failed");
                Check(!done || result == 2, "finished entrance restarted");
                if (result == 2) { done = true; Check(Math.Abs(feet.y) < .001f && Math.Abs(feet.z - 308.53f) < .01f, "landing doesn't match the stock exit anchor"); }
                n = Native.sms_view_draws(view, draws, 32);
                Check(draws.Take((int)n).Select(d => d.key).Distinct().Count() == n, "statue and cap share an instance key");
                gotCapInHand |= draws.Take((int)n).Any(d => d.kind == 15);
                var fx = Native.sms_view_fx_batches(view, out var count);
                uint colored = 0;
                for (int j = 0; j < count; j++) {
                    var b = Marshal.PtrToStructure<Native.FxBatch>(IntPtr.Add(fx, j * Marshal.SizeOf<Native.FxBatch>()));
                    if (b.texture == 0) { colored += b.vertex_count; Check(b.blend == 1, "colored Mario particles add white"); }
                }
                if (time < 1.4f) particles |= colored > 0;
                if (time >= 1.5f) Check(colored == 0, "particles survive the completed Mario reveal");
                var ptr = Native.sms_view_positions(view, out var countVertices);
                var xyz = new float[countVertices * 3]; Marshal.Copy(ptr, xyz, 0, xyz.Length);
                Check(xyz.All(float.IsFinite), "invalid statue/Mario geometry");
            }
            Check(particles && gotCapInHand && done, "entrance missed particles, cap-in-hand landing or completion");
            Check(Native.sms_view_texture_version(view, (uint)texture) > version, "portal texture did not animate");
            Check(Native.sms_view_entrance_update(view, float.NaN, 1, ref f, out _) == 0, "invalid entrance time accepted");
        } finally { Native.sms_view_free(view); }
    }

    static void CheckPortalPlayback()
    {
        if (!File.Exists(Path.Combine(assets, "portal", "05_gate01.bmd"))) { Console.WriteLine("SKIP: statue portal cache absent"); return; }
        var view = Native.sms_view_entrance_fast_new(NativeLib.CString(assets), 0);
        var reference = Native.sms_view_entrance_fast_new(NativeLib.CString(assets), 0);
        Check(view != IntPtr.Zero && reference != IntPtr.Zero, "portal playback views couldn't load");
        try {
            var playback = new MarioPortalPlayback();
            var camera = new Native.ViewFrame { forward = new Native.Vec3(0, 0, 1), up = new Native.Vec3(0, 1, 0) };
            var feet = default(Native.Vec3);
            bool Sample(float elapsed, bool activated, bool handedOff, bool cut, bool visible)
            {
                if (!playback.Sample(elapsed, activated, handedOff, cut, 1.55f, visible)) return false;
                Check(Native.sms_view_entrance_update(view, playback.Time, playback.State, ref camera, out feet) != 0, "portal playback update failed");
                playback.Updated();
                return true;
            }
            Check(Sample(.25f, false, false, false, false), "offscreen initial geometry wasn't initialized");
            var draws = new ViewDraw[32];
            uint n = Native.sms_view_draws(view, draws, 32);
            var gate = draws.Take((int)n).Single(d => d.kind == 241);
            // Keep the host's padded frustum bound honest against the disc model.
            var pointer = Native.sms_view_positions(view, out var vertices);
            var xyz = new float[vertices * 3]; Marshal.Copy(pointer, xyz, 0, xyz.Length);
            uint end = vertices;
            foreach (var d in draws.Take((int)n)) if (d.first_vertex > gate.first_vertex) end = Math.Min(end, d.first_vertex);
            for (uint i = gate.first_vertex; i < end; i++)
                Check(Math.Abs(xyz[i * 3]) <= 205 && xyz[i * 3 + 1] >= -5 && xyz[i * 3 + 1] <= 405
                    && Math.Abs(xyz[i * 3 + 2] - 3.5f) <= 10, "M quad escaped the host frustum bound");
            Native.sms_view_model(view, gate.model, out var model);
            var batches = new ViewBatch[model.batch_count]; Native.sms_view_batches(view, gate.model, batches, model.batch_count);
            uint texture = (uint)batches[0].texture;
            byte[] Image(IntPtr v)
            {
                Native.sms_view_texture(v, texture, out var info, null, 0);
                var pixels = new byte[info.width * info.height * 4];
                Native.sms_view_texture(v, texture, out _, pixels, (uint)pixels.Length);
                return pixels;
            }
            uint version = Native.sms_view_texture_version(view, texture);
            for (int i = 1; i <= 300; i++) Check(!Sample(.25f + i / 60f, false, false, false, false), "hidden idle portal kept sampling");
            Check(Native.sms_view_texture_version(view, texture) == version, "hidden idle portal changed texture version");
            Check(Sample(8.37f, false, false, false, true) && playback.Time == 8.37f, "idle resume replayed missed time");
            Native.sms_view_entrance_update(reference, 8.37f, 0, ref camera, out _);
            Check(Image(view).SequenceEqual(Image(reference)), "idle resume differs from direct current-frame sampling");
            for (int i = 0; i <= 93; i++) Check(Sample(i / 60f, true, false, false, false), "offscreen exit/control processing was culled");
            var safeFeet = feet;
            Check(Sample(1.58f, true, false, true, false) && playback.Time == 1.55f, "slope-safe pose wasn't held before handoff");
            Check(feet.x == safeFeet.x && feet.y == safeFeet.y && feet.z == safeFeet.z, "slope-safe rider pose moved while held");
            var activeImage = Image(view);
            Check(Sample(1.6f, true, true, true, false) && playback.Time == 1.6f && playback.State == 2,
                "hidden handoff froze the portal clock or failed to retire the rider");
            n = Native.sms_view_draws(view, draws, 32);
            Check(n == 2 && draws.Take((int)n).All(d => d.kind >= 240), "offscreen handoff left the native rider visible");
            Native.sms_view_fx_info(view, out var fx);
            Check(fx.vertices == 0, "offscreen handoff retained portal exit particles");
            Check(Sample(1.95f, true, true, true, true), "visible spent portal stopped sampling");
            Native.sms_view_entrance_update(reference, 1.95f, 0, ref camera, out _);
            Check(Image(view).SequenceEqual(Image(reference)), "visible slope-cut portal stayed on Delfino after closing");
            version = Native.sms_view_texture_version(view, texture);
            for (int i = 1; i <= 300; i++) Check(!Sample(1.95f + i / 60f, true, true, true, false), "hidden spent portal kept sampling");
            Check(Native.sms_view_texture_version(view, texture) == version, "hidden spent portal changed texture version");
            Check(Sample(31.27f, true, true, true, true) && playback.Time == 31.27f, "spent resume replayed or clamped time");
            Native.sms_view_entrance_update(reference, 31.27f, 0, ref camera, out _);
            Check(Image(view).SequenceEqual(Image(reference)), "slope-cut handoff didn't restore current swirling colors");
            Check(!activeImage.SequenceEqual(Image(view)), "Delfino image remained after handoff");
            Console.WriteLine("PASS: slope-safe clock/handoff, hidden idle/spent culling, live offscreen exit and direct-frame resume");
        } finally { if (view != IntPtr.Zero) Native.sms_view_free(view); if (reference != IntPtr.Zero) Native.sms_view_free(reference); }
    }

    static void CheckFastEntrance(bool luigi)
    {
        if (!File.Exists(Path.Combine(assets, "portal", "05_gate01.bmd"))) { Console.WriteLine("SKIP: statue portal cache absent"); return; }
        var message = new byte[4096];
        Check(Native.sms_prepare_entrance(NativeLib.CString(assets), message, (uint)message.Length) != 0, "portal cache preparation failed");
        Check(Native.sms_entrance_cache_complete(NativeLib.CString(assets)) != 0, "portal cache not reusable");
        var view = Native.sms_view_entrance_fast_new(NativeLib.CString(assets), 0);
        Check(view != IntPtr.Zero, "fast entrance couldn't load");
        try {
            if (luigi) {
                var melee = Path.GetFullPath(Path.Combine(assets, "..", "..", "libssbm", "assets"));
                if (!Directory.Exists(melee)) { Console.WriteLine("SKIP: Luigi Melee cache absent"); return; }
                Check(Native.sms_view_set_luigi(view, NativeLib.CString(melee)) != 0, "Luigi entrance mesh couldn't load");
            }
            var camera = new Native.ViewFrame { forward = new Native.Vec3(0, 0, 1), up = new Native.Vec3(0, 1, 0) };
            var draws = new ViewDraw[32];
            Native.sms_view_entrance_update(view, .25f, 0, ref camera, out _);
            uint n = Native.sms_view_draws(view, draws, 32);
            Check(n == 2 && draws.Take((int)n).All(d => d.kind >= 240 && (d.flags & 4) == 0), "first static pose wasn't uploaded");
            Native.sms_view_entrance_update(view, .26f, 0, ref camera, out _);
            Native.sms_view_positions(view, out var idleVertices);
            Check(idleVertices == 0, "idle statue rebuilt geometry");
            if (luigi) {
                // Pod preparation can precede its passenger. Preload the model table, then
                // select Luigi after idle frames, as the adapter does when the rider arrives.
                var modelCount = Native.sms_view_model_count(view);
                Native.sms_view_skin(view, 1);
                Native.sms_view_entrance_update(view, .27f, 0, ref camera, out _);
                Check(Native.sms_view_model_count(view) == modelCount, "late rider selection changes the fixed model table");
            }
            Native.sms_view_fx_texture(view, 0, out var paletteInfo, null, 0);
            var palette = new byte[paletteInfo.width * paletteInfo.height * 4];
            Native.sms_view_fx_texture(view, 0, out _, palette, (uint)palette.Length);
            int red = 0, green = 0;
            for (int i = 0; i < palette.Length; i += 4) {
                if (palette[i+3] < 128) continue;
                if (palette[i] > palette[i+1] * 3 && palette[i] > palette[i+2] * 3) red++;
                if (palette[i+1] > palette[i] * 3 && palette[i+1] > palette[i+2] * 3) green++;
            }
            Check(luigi ? green > red : red > green, "wrong red/green particle palette for rider");
            bool done = false, particles = false, rider = false;
            uint gateTexture = 0;
            for (int i = 0; i <= 180; i++) {
                float time = i / 60f;
                var result = Native.sms_view_entrance_update(view, time, done ? (byte)2 : (byte)1, ref camera, out var feet);
                Check(result != 0, "fast entrance update failed");
                n = Native.sms_view_draws(view, draws, 32);
                Check(draws.Take((int)n).Select(d => d.key).Distinct().Count() == n, "Luigi/static model instance keys collide");
                var gate = draws.Take((int)n).Single(d => d.kind == 241);
                Native.sms_view_model(view, gate.model, out var gateInfo);
                var batches = new ViewBatch[gateInfo.batch_count]; Native.sms_view_batches(view, gate.model, batches, gateInfo.batch_count);
                Check(batches.All(b => b.blend == ViewBlend.Translucent), "smooth M edge was assigned a hard cutout material");
                gateTexture = (uint)batches[0].texture;
                if (!done) {
                    rider |= draws.Take((int)n).Any(d => luigi ? d.model == Native.sms_view_model_count(view) - 1 : d.model == 0);
                    if (!luigi) Check(draws.Take((int)n).All(d => d.kind != 15), "fast exit played held-cap landing");
                } else {
                    Native.sms_view_positions(view, out var vertices);
                    Check(vertices == 0 && n == 2 && draws.Take((int)n).All(d => (d.flags & 4) != 0), "spent entrance still poses Mario/Luigi or static geometry");
                }
                var fx = Native.sms_view_fx_batches(view, out var count); uint colored = 0;
                for (int j = 0; j < count; j++) {
                    var b = Marshal.PtrToStructure<Native.FxBatch>(IntPtr.Add(fx, j * Marshal.SizeOf<Native.FxBatch>()));
                    if (b.texture == 0) { colored += b.vertex_count; Check(b.blend == 1, "particle palette blends additively"); }
                }
                particles |= colored > 0;
                if (time >= .8f) Check(colored == 0, "colored particles linger after materialization");
                if (result == 2) {
                    Check(time >= 1.74f && time <= 3f && Math.Abs(feet.y) < .001f, "wrong fall/completion timing"); done = true;
                }
            }
            Check(done && particles && rider, "fast exit missed rider, particles or completion");
            Native.sms_view_entrance_update(view, 3, 2, ref camera, out _);
            Native.sms_view_texture(view, gateTexture, out var ti, null, 0);
            var returned = new byte[ti.width * ti.height * 4]; Native.sms_view_texture(view, gateTexture, out _, returned, (uint)returned.Length);
            Check(ti.width == 256 && ti.height == 256, "portal used the low-resolution cache");
            Check(Enumerable.Range(0, returned.Length / 4).Any(i => returned[i * 4 + 3] > 0 && returned[i * 4 + 3] < 255), "portal cache lost smooth edge coverage");
            Native.sms_view_entrance_update(view, 3, 0, ref camera, out _);
            var idleImage = new byte[returned.Length]; Native.sms_view_texture(view, gateTexture, out _, idleImage, (uint)idleImage.Length);
            Check(returned.SequenceEqual(idleImage), "portal didn't return to original swirling colors");
            Console.WriteLine("PASS: fast " + (luigi ? "late-selected Luigi/green" : "Mario/red") + " exit, static retention, timing, control handoff and portal reset");
        } finally { Native.sms_view_free(view); }
    }
}
