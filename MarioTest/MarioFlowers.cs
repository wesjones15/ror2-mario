using System;
using System.Collections.Generic;
using Ror2Common;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace MarioTest
{
    // Sunshine's sambo flowers, rare finds on a stage (libsms::flower, Enemy/hanasambo.cpp):
    //  * a ring of flowers (TSamboFlowerCoinUnit) that FLUDD sprays open: when every one is open at
    //    once a ring of coins is thrown out of the middle; miss one and the others wilt (bloom timer),
    //  * a lone flower, which pays one coin,
    //  * a ring of hopping flower heads (TSamboHead) that chase Mario and bite,
    //  * a flower tower (THanaSambo) that bursts out of the ground near Mario, bites and sinks again.
    // Stomping or pounding a flower enemy kills it (coins drop); a spray freezes a tower for a while.
    //
    // The world lives in the sim of the Mario the host's player plays, and only on the machine that
    // runs the server (the coins are the server's, as every other coin is): in a co-op run the
    // other players do not see them. Models, clips and tuning come from the user's disc
    // (assets/enemy/sambo*, params_enemy/*.prm). Each thing on screen has its own native prop view
    // (one posed instance per view), made when it comes near and freed when it leaves, so a stage
    // with a dozen of them stays cheap.
    internal sealed class MarioFlowers : MonoBehaviour
    {
        // The three kinds (Native.FlowerItem.kind) and the look codes of their views.
        const byte Flower = 0, Tower = 1, Head = 2;
        static readonly byte[] Looks = { 5, 6, 7 };
        const int MaxViews = 16;
        const float DrawRange = 90f, MinSpawnDistance = 18f, LingerSeconds = 3f;

        // Clips by kind, in the order the pose code names them.
        static readonly string[][] ClipNames =
        {
            new[] { "flower_wait", "flower_hit", "flower_fwait" },
            new[] { "sambo_fset", "sambo_fwait", "sambo_fattack_start", "sambo_fattack", "sambo_fattack_end",
                    "sambo_fhide", "sambo_hit", "sambo_down" },
            new[] { "sambohead_set", "sambohead_wait", "sambohead_jump_start", "sambohead_jump_end",
                    "sambohead_land", "sambohead_hit", "sambohead_down" },
        };

        sealed class Prop
        {
            public IntPtr view;
            public PropSource source;
            public ModelView models;
            public int[] clip;
            public float[] frames;
            public float seen;
        }

        readonly Dictionary<(byte, uint), Prop> props = new Dictionary<(byte, uint), Prop>();
        readonly List<(byte, uint)> stale = new List<(byte, uint)>();
        readonly List<(uint spawn, Vector3 pos, float yaw)> placements = new List<(uint, Vector3, float)>();
        readonly Native.FlowerEvent[] events = new Native.FlowerEvent[64];
        Native.FlowerClips clips;
        bool clipsRead, placed, failed;
        IntPtr simUsed;
        float arrived;

        void Awake()
        {
            SceneManager.activeSceneChanged += SceneChanged;
            Run.onRunDestroyGlobal += RunEnded;
        }

        void OnDestroy()
        {
            SceneManager.activeSceneChanged -= SceneChanged;
            Run.onRunDestroyGlobal -= RunEnded;
            Reset();
        }

        void SceneChanged(Scene from, Scene to) => Reset();
        void RunEnded(Run run) => Reset();

        void Reset()
        {
            foreach (var p in props.Values) Free(p);
            props.Clear();
            placements.Clear();
            placed = false;
            simUsed = IntPtr.Zero;
            arrived = 0f;
        }

        static void Free(Prop p)
        {
            p.models?.Dispose();
            if (p.view != IntPtr.Zero) Native.sms_view_free(p.view);
            p.view = IntPtr.Zero;
        }

        /// Clip lengths in ticks, read once from the models' own clips.
        void ReadClips()
        {
            clipsRead = true;
            uint Ticks(byte look, string clip)
            {
                var v = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, look);
                if (v == IntPtr.Zero) return 0;
                try
                {
                    return Native.sms_view_prop_clip(v, NativeLib.CString(clip), out float frames) >= 0
                        ? (uint)Mathf.RoundToInt(frames * 2f) : 0u;
                }
                finally { Native.sms_view_free(v); }
            }
            clips = new Native.FlowerClips
            {
                flower_open = Ticks(5, "flower_hit"),
                tower_appear = Ticks(6, "sambo_fset"),
                tower_hide = Ticks(6, "sambo_fhide"),
                tower_attack_start = Ticks(6, "sambo_fattack_start"),
                tower_attack = Ticks(6, "sambo_fattack"),
                tower_attack_end = Ticks(6, "sambo_fattack_end"),
                tower_freeze = Ticks(6, "sambo_hit") * 3,
                tower_die = Ticks(6, "sambo_down"),
            };
        }

        bool Suitable()
        {
            var info = SceneInfo.instance;
            return Run.instance && Stage.instance && info && info.groundNodes && info.sceneDef
                && info.sceneDef.sceneType == SceneType.Stage;
        }

        void FixedUpdate()
        {
            if (failed || !NetworkServer.active || !Plugin.AssetsReady || PauseManager.isPaused || !Suitable()) return;
            if (Plugin.FlowerChance.Value <= 0) return;
            var driver = MarioDriver.Player;
            if (driver == null || driver.SimHandle == IntPtr.Zero || !driver.Alive) return;
            if (arrived == 0f) arrived = Time.time;
            if (Time.time - arrived < 2f) return;
            var sim = driver.SimHandle;
            if (!clipsRead) ReadClips();
            if (!placed)
            {
                placed = true;
                if (!Place(driver, sim)) return;
            }
            else if (sim != simUsed)
            {
                // A new sim (after a death or a restart): the same flowers, shut again.
                Setup(sim);
                foreach (var p in placements)
                {
                    var at = UnityHost.ToSim(p.pos);
                    Native.sms_flowers_place(sim, p.spawn, ref at, p.yaw);
                }
            }
            simUsed = sim;
            Pump(driver, sim);
        }

        void Setup(IntPtr sim)
        {
            Native.sms_flowers_setup(sim, NativeLib.CString(Plugin.AssetsDir), ref clips);
            Native.sms_flowers_set_table(sim, (uint)Plugin.FlowerChance.Value, 3, 5, 2, 2);
        }

        /// The stage's rolls: each may put one thing on a ground node away from where Mario stands.
        bool Place(MarioDriver driver, IntPtr sim)
        {
            Setup(sim);
            var graph = SceneInfo.instance.groundNodes;
            var nodes = graph.GetActiveNodesForHullMask(HullMask.Human);
            if (nodes == null || nodes.Count == 0) return false;
            var from = driver.Feet;
            int made = 0;
            for (int roll = 0; roll < Plugin.FlowerRolls.Value; roll++)
            {
                uint spawn = Native.sms_flowers_roll(sim, UnityEngine.Random.value, UnityEngine.Random.value);
                if (spawn == 0) continue;
                for (int tries = 0; tries < 40; tries++)
                {
                    if (!graph.GetNodePosition(nodes[UnityEngine.Random.Range(0, nodes.Count)], out Vector3 pos)) continue;
                    if ((pos - from).sqrMagnitude < MinSpawnDistance * MinSpawnDistance) continue;
                    float yaw = UnityEngine.Random.Range(0f, 360f);
                    var at = UnityHost.ToSim(pos);
                    if (Native.sms_flowers_place(sim, spawn, ref at, yaw) == 0) break;
                    placements.Add((spawn, pos, yaw));
                    made++;
                    break;
                }
            }
            Plugin.Log.LogInfo($"Sunshine flowers: {made} set(s) placed on this stage ({Plugin.FlowerRolls.Value} rolls at "
                + $"{Plugin.FlowerChance.Value}/1000)");
            return true;
        }

        /// What the sim's flowers did since the last step: coins, bites, deaths, sounds.
        void Pump(MarioDriver driver, IntPtr sim)
        {
            uint n;
            while ((n = Native.sms_flowers_events(sim, events, (uint)events.Length)) > 0)
            {
                for (int i = 0; i < n; i++) Handle(driver, sim, events[i]);
                if (n < events.Length) break;
            }
        }

        void Handle(MarioDriver driver, IntPtr sim, Native.FlowerEvent e)
        {
            var body = driver.Body;
            var at = new Vector3(e.pos.x, e.pos.y, e.pos.z) * UnityHost.Scale;
            switch (e.kind)
            {
                case 1: Sound("MSD_SE_OBJ_FLOWER_OPEN_" + Mathf.Clamp(e.a, 0, 11), e.pos); break;
                case 3:
                    if (body && body.teamComponent && MarioCoins.Instance)
                    {
                        MarioCoins.Instance.Burst(at, e.vel, body.teamComponent.teamIndex);
                        Sound("MSD_SE_SY_COIN_APPEAR", e.pos);
                    }
                    break;
                case 4: Sound("MSD_SE_EN_SAMBO_APPEAR", e.pos); break;
                case 6:
                    if (body && body.healthComponent && body.healthComponent.alive)
                    {
                        body.healthComponent.TakeDamage(new DamageInfo
                        {
                            damage = body.maxHealth * Plugin.FlowerBiteMax.Value / 100f,
                            position = body.corePosition,
                            procCoefficient = 0f,
                            damageColorIndex = DamageColorIndex.Default,
                        });
                        var from = e.pos;
                        Native.sms_sim_knock_down(sim, ref from, 0);
                    }
                    break;
                case 7:
                    // genRandomItem: a handful of coins from where it fell.
                    if (body && body.teamComponent && MarioCoins.Instance)
                    {
                        int coins = e.a == 1 ? 5 : 2;
                        for (int c = 0; c < coins; c++)
                        {
                            float a = c * Mathf.PI * 2f / coins;
                            MarioCoins.Instance.Burst(at + Vector3.up * 1f,
                                new Native.Vec3(Mathf.Cos(a) * 2f, 10f + c, Mathf.Sin(a) * 2f), body.teamComponent.teamIndex);
                        }
                        Sound("MSD_SE_SY_COIN_APPEAR", e.pos);
                    }
                    break;
            }
        }

        static void Sound(string name, Native.Vec3 pos)
        {
            var player = MarioView.Player;
            if (player == null || player.Handle == IntPtr.Zero) return;
            Native.sms_view_sound(player.Handle, NativeLib.CString(name), ref pos);
        }

        // ---- drawing ----

        void LateUpdate()
        {
            var driver = MarioDriver.Player;
            if (driver == null || driver.SimHandle == IntPtr.Zero || (!placed && props.Count == 0)) return;
            var sim = driver.SimHandle;
            var cam = GameSettings.PlayerCamera();
            var eye = cam ? cam.position : driver.Feet;
            float now = Time.time;
            for (byte kind = 0; kind < 3; kind++)
            {
                uint count = Native.sms_flowers_count(sim, kind);
                for (uint i = 0; i < count; i++)
                {
                    if (Native.sms_flowers_get(sim, kind, i, out var item) == 0) continue;
                    if (!Visible(kind, item.state)) continue;
                    var at = new Vector3(item.pos.x, item.pos.y, item.pos.z) * UnityHost.Scale;
                    if ((at - eye).sqrMagnitude > DrawRange * DrawRange) continue;
                    var key = (kind, item.handle);
                    if (!props.TryGetValue(key, out var prop))
                    {
                        if (props.Count >= MaxViews || (prop = Make(kind)) == null) continue;
                        props[key] = prop;
                    }
                    prop.seen = now;
                    Draw(prop, kind, item);
                }
            }
            stale.Clear();
            foreach (var kv in props)
                if (now - kv.Value.seen > LingerSeconds) stale.Add(kv.Key);
            foreach (var key in stale) { Free(props[key]); props.Remove(key); }
        }

        /// Hidden towers and sleeping heads are not drawn (under the ground / not there yet).
        static bool Visible(byte kind, byte state) =>
            kind == Flower || (kind == Tower ? state != 0 && state != 9 : state != 0 && state != 4);

        Prop Make(byte kind)
        {
            var view = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, Looks[kind]);
            if (view == IntPtr.Zero)
            {
                failed = true;
                Plugin.Log.LogInfo("The Sunshine flower models aren't in the assets folder (they are fetched from the " +
                    "stage archives on the first run after an update: set Paths.Disc to your Sunshine disc image " +
                    "and restart); no flowers this run.");
                return null;
            }
            var names = ClipNames[kind];
            var prop = new Prop { view = view, clip = new int[names.Length], frames = new float[names.Length] };
            for (int i = 0; i < names.Length; i++)
                prop.clip[i] = Native.sms_view_prop_clip(view, NativeLib.CString(names[i]), out prop.frames[i]);
            prop.source = new PropSource(view);
            prop.models = ModelView.Create(prop.source, "Sunshine flowers");
            if (prop.models == null) { Free(prop); return null; }
            return prop;
        }

        /// The clip (index into ClipNames[kind]) and its frame for a state and the ticks spent in it.
        static void Pose(byte kind, byte state, uint age, out int clip, out bool reverse, out bool loop)
        {
            reverse = false;
            loop = false;
            clip = 0;
            switch (kind)
            {
                case Flower: // Closed, Opening, Open, Closing
                    clip = state == 0 ? 0 : state == 2 ? 2 : 1;
                    reverse = state == 3;
                    loop = state == 0 || state == 2;
                    break;
                case Tower: // Hidden, Appearing, Waiting, AttackStart, AttackLoop, AttackEnd, Hiding, Frozen, Dying, Dead
                    switch (state)
                    {
                        case 1: clip = 0; break;
                        case 2: clip = 1; loop = true; break;
                        case 3: clip = 2; break;
                        case 4: clip = 3; loop = true; break;
                        case 5: clip = 4; break;
                        case 6: clip = 5; break;
                        case 7: clip = 6; break;
                        default: clip = 7; break;
                    }
                    break;
                default: // Dormant, Ready, Hopping, Knocked, Dead
                    switch (state)
                    {
                        case 1: clip = age < 240 ? 0 : 1; loop = age >= 240; break;
                        case 2: clip = 2; break;
                        case 3: clip = 5; break;
                        default: clip = 6; break;
                    }
                    break;
            }
        }

        void Draw(Prop prop, byte kind, Native.FlowerItem item)
        {
            Pose(kind, item.state, item.age, out int clip, out bool reverse, out bool loop);
            float frames = prop.frames[clip];
            float frame = item.age * 0.5f;
            if (frames > 1f)
            {
                frame = loop ? frame % frames : Mathf.Min(frame, frames - 1f);
                if (reverse) frame = frames - 1f - frame;
            }
            short yaw = (short)Mathf.RoundToInt(item.yaw / 180f * 32768f);
            Native.sms_view_prop_update(prop.view, yaw, prop.clip[clip], frame, 1);
            var rot = Quaternion.Euler(0f, item.yaw, 0f);
            prop.source.draw = new ViewDraw
            {
                key = 1, model = 0, pos = new ViewVec3 { x = item.pos.x, y = item.pos.y, z = item.pos.z },
                rot_x = rot.x, rot_y = rot.y, rot_z = rot.z, rot_w = rot.w, alpha = 1f,
            };
            prop.models.Update(UnityHost.Scale);
        }

        // One native prop view, one host-placed instance (the Shine's way).
        sealed class PropSource : IViewSource
        {
            readonly SmsViewSource native;
            public ViewDraw draw;
            public PropSource(IntPtr view) { native = new SmsViewSource(view); }
            public uint ModelCount() => native.ModelCount();
            public bool Model(uint m, out ViewModelInfo info) => native.Model(m, out info);
            public uint Batches(uint m, ViewBatch[] o, uint max) => native.Batches(m, o, max);
            public uint Indices(uint m, int[] o, uint max) => native.Indices(m, o, max);
            public uint Uvs(uint m, float[] o, uint max) => native.Uvs(m, o, max);
            public bool Texture(uint id, out ViewTexture info, byte[] rgba, uint len) => native.Texture(id, out info, rgba, len);
            public uint Draws(ViewDraw[] output, uint max)
            {
                if (output == null) return 1;
                if (max == 0) return 0;
                output[0] = draw;
                return 1;
            }
            public IntPtr Positions(out uint count) => native.Positions(out count);
            public IntPtr Normals(out uint count) => native.Normals(out count);
            public IntPtr Colors(out uint count) => native.Colors(out count);
            public IntPtr TextureIds(out uint count) => native.TextureIds(out count);
            public IntPtr FrameUvs(out uint count) => native.FrameUvs(out count);
        }
    }
}
