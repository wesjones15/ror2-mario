using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BepInEx.Configuration;
using RoR2;
using Ror2Common;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MarioTest
{
    // The goop world Bowser Jr. paints (libsms goop.rs, goop_critters.rs, libsms-ffi goop.rs):
    // one native world for the process, handed to every Mario's sim (he stands in it, gets goopy,
    // and FLUDD cleans it) and to Bowser Jr.'s (he paints it). The server steps it while a
    // Bowser Jr. is in play: the goop's lifetime, the Swoopin' Stus that grow out of it near him
    // and the goop Piranhas his seeds grow (`TNameKuri`, `TPakkun`), with the stage's enemies as
    // their targets. Enemies standing in goop take its kind's harm (a host choice: the game's goop
    // only hurts Mario), the critters' hits land as Bowser Jr.'s damage, a burst Piranha throws
    // coins, and the world's sounds and particles play here. Every machine draws the world's
    // goop as textured patches over its chunks (sms_goop_chunk_image_at, each kind's look from the
    // disc swimming as the pollution layers' btk do) and the critters as posed props.
    //
    // Multiplayer (KoopaJrGoopNet.cs): the server's world is the real one; each client draws and
    // stands in a mirror of it and sends what its own sims did to it for the server to redo.
    internal sealed partial class KoopaJrGoop : MonoBehaviour
    {
        const int MaxViews = 24, MaxCritters = 64, MaxTargets = 128, MaxChunks = 256;
        const float DrawRange = 90f, TargetRange = 80f, LingerSeconds = 2f;
        // Chunk pictures: pixels a side, how often the nearby ones are redrawn to swim, and how
        // many uploads a frame.
        const int ImageRes = 64, UploadsPerFrame = 6;
        const float SwimEvery = 0.15f, SwimRange = 30f;
        const float ChunkUnits = 512f, TexelUnits = 32f;
        // The surface sits this far (metres) over the stage's own ground under each corner, found
        // by a ray from `ConformAbove` over the goop's painted height; ground more than
        // `ConformBand` from that height is another floor and the painted height is kept.
        const float Lift = 0.04f, ConformAbove = 1.5f, ConformBand = 1.5f;

        static readonly string[] StuClips = { "name_down", "name_hit", "name_jump_end", "name_jump_start", "name_land",
            "name_set", "name_wait", "name_walk" };
        static readonly string[] PiranhaClips = { "pakun_crush_to_hide", "pakun_damage", "pakun_down", "pakun_hide",
            "pakun_hit", "pakun_hit_end", "pakun_hit_start", "pakun_set", "pakun_shoot", "pakun_wait" };
        // Clips that loop: a Stu's wait and walk, a Piranha's wait.
        static readonly bool[] StuLoops = { false, false, false, false, false, false, true, true };
        static readonly bool[] PiranhaLoops = { false, false, false, false, false, false, false, false, false, true };
        // The Gatekeeper's clips in its changeBck order (goop_critters::keeper_clip); wait1/2,
        // wait2_loop, wait2_openloop and wait3 loop.
        static readonly string[] KeeperClips = { "gene_pakkun_appear1", "gene_pakkun_appear1_end", "gene_pakkun_appear1_start",
            "gene_pakkun_damage1", "gene_pakkun_damage2", "gene_pakkun_dead1", "gene_pakkun_dive1", "gene_pakkun_iyaiya1",
            "gene_pakkun_shoot1", "gene_pakkun_towait2", "gene_pakkun_wait1", "gene_pakkun_wait2", "gene_pakkun_wait2_close",
            "gene_pakkun_wait2_end", "gene_pakkun_wait2_loop", "gene_pakkun_wait2_open", "gene_pakkun_wait2_openloop",
            "gene_pakkun_wait2_start", "gene_pakkun_wait3" };
        static readonly bool[] KeeperLoops = { false, false, false, false, false, false, false, false, false, false, true, true,
            false, false, true, false, true, false, true };

        public static ConfigEntry<float> GoopDamage, CritterDamage, ContactPeriod, GateKeeperSeconds;
        public static ConfigEntry<int> GateKeepers;
        public static ConfigEntry<bool> HurtEnemies;
        public static ConfigEntry<float> SwirlRefraction;

        public static void Bind(ConfigFile config)
        {
            HurtEnemies = config.Bind("BowserJr", "GoopHurtsEnemies", true,
                "Enemies standing in Bowser Jr.'s goop take its kind's harm (brown: slowed, fire: burnt, " +
                "electric: shocked, poison: poisoned, deep: heavy damage). A host choice; the game's goop only hurts Mario.");
            GoopDamage = config.Bind("BowserJr", "GoopDamage", 0.5f,
                "Bowser Jr.'s damage times this, each time an enemy is found standing in harmful goop (scaled by kind).");
            ContactPeriod = config.Bind("BowserJr", "GoopContactSeconds", 0.5f,
                "How often an enemy standing in goop is hurt by it.");
            SwirlRefraction = config.Bind("BowserJr", "SwirlRefraction", 1f,
                "How strongly Shadow Mario warps the scene behind him (RoR2's screen refraction over his body, " +
                "the disc's scrolling warp as its map). 0: off (his baked swirl only).");
            CritterDamage = config.Bind("BowserJr", "CritterDamage", 1.5f,
                "Bowser Jr.'s damage times this, for each hit by a Swoopin' Stu, a goop Piranha or a goop ball.");
            GateKeepers = config.Bind("BowserJr", "GateKeepers", 1,
                "How many big goop Piranhas (the Gatekeeper) may be up at once. One grows out of a big pile of goop " +
                "near Bowser Jr., hurls goop at enemies from its mouth and dives back in when its time is up or its " +
                "pile is cleaned. 0: none. A host choice.");
            GateKeeperSeconds = config.Bind("BowserJr", "GateKeeperSeconds", 20f,
                "How long a Gatekeeper stays up.");
        }

        /// The world, once its disc files have been read (zero before that).
        public static IntPtr World => ready ? world : IntPtr.Zero;

        static IntPtr world;
        static volatile bool ready;
        static bool started, failed;
        static Native.Host host;
        static KoopaJrGoop instance;

        sealed class Prop
        {
            public IntPtr view, brainView;
            public MarioFlowers.PropSource source, brainSource;
            public ModelView models, brainModels;
            public int[] clip;
            public float[] frames;
            public float seen;
        }

        sealed class Chunk
        {
            public GameObject go;
            public Mesh mesh;
            public Texture2D tex;
            public Material mat;
            public uint version;
            public bool built;
            public float drawnAt;
            public bool seen;
        }

        readonly Dictionary<ulong, Prop> props = new Dictionary<ulong, Prop>();
        readonly List<ulong> stale = new List<ulong>();
        readonly Dictionary<(int, int), Chunk> chunks = new Dictionary<(int, int), Chunk>();
        readonly List<(int, int)> staleChunks = new List<(int, int)>();
        readonly Native.GoopEvent[] events = new Native.GoopEvent[128];
        readonly Native.Critter[] critters = new Native.Critter[MaxCritters];
        readonly Native.GoopTarget[] targets = new Native.GoopTarget[MaxTargets];
        readonly Dictionary<ulong, HealthComponent> byId = new Dictionary<ulong, HealthComponent>();
        readonly ulong[] contactIds = new ulong[MaxTargets];
        readonly byte[] contactKinds = new byte[MaxTargets];
        readonly List<CharacterBody> jrs = new List<CharacterBody>();
        Native.Vec3[] feet = new Native.Vec3[16];
        readonly int[] keys = new int[MaxChunks * 2];
        readonly uint[] versions = new uint[MaxChunks];
        readonly byte[] density = new byte[256], kinds = new byte[256];
        readonly float[] heights = new float[256];
        readonly byte[] image = new byte[ImageRes * ImageRes * 4];
        readonly Dictionary<IntPtr, byte[]> names = new Dictionary<IntPtr, byte[]>();
        IntPtr fxView;
        MarioFx fx;
        bool fxTried;
        float fxUntil;
        GameObject root;

        void Awake()
        {
            instance = this;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            SceneManager.activeSceneChanged -= SceneChanged;
            ClearDrawn();
            fx?.Dispose();
            fx = null;
            if (fxView != IntPtr.Zero) Native.sms_view_free(fxView);
            fxView = IntPtr.Zero;
        }

        // A new stage starts with no goop (the world's disc files are kept).
        void SceneChanged(Scene from, Scene to)
        {
            if (ready) Native.sms_goop_clear(world);
            ClearDrawn();
            NetReset();
        }

        void ClearDrawn()
        {
            foreach (var p in props.Values) Free(p);
            props.Clear();
            foreach (var c in chunks.Values) Free(c);
            chunks.Clear();
        }

        static void Free(Prop p)
        {
            p.models?.Dispose();
            p.brainModels?.Dispose();
            if (p.view != IntPtr.Zero) Native.sms_view_free(p.view);
            if (p.brainView != IntPtr.Zero) Native.sms_view_free(p.brainView);
            p.view = p.brainView = IntPtr.Zero;
        }

        static void Free(Chunk c)
        {
            if (c.go) Destroy(c.go);
            if (c.mesh) Destroy(c.mesh);
            if (c.tex) Destroy(c.tex);
            if (c.mat) Destroy(c.mat);
        }

        /// Reads the world's disc files on a worker the first time a Bowser Jr. is in play (the
        /// stamps, the sign, the critters' tuning and clips, each kind's baked look).
        static void Begin(string assets)
        {
            if (started || failed) return;
            started = true;
            world = Native.sms_goop_new();
            if (world == IntPtr.Zero) { failed = true; return; }
            host = UnityHost.Table();
            var dir = NativeLib.CString(assets);
            var w = world;
            Task.Run(() =>
            {
                try
                {
                    Native.sms_goop_setup(w, dir);
                    Native.sms_goop_keeper_tune(w, (uint)Mathf.Clamp(GateKeepers.Value, 0, 4), 0,
                        (uint)Mathf.Clamp(GateKeeperSeconds.Value * 120f, 120f, 120f * 600f));
                    ready = true;
                }
                catch (Exception e)
                {
                    failed = true;
                    Plugin.Log.LogError($"Bowser Jr.'s goop world: {e.Message}");
                }
            });
            Plugin.Log.LogInfo("Bowser Jr.'s goop world: reading its disc files");
        }

        int GatherJrs()
        {
            jrs.Clear();
            foreach (var b in CharacterBody.readOnlyInstancesList)
                if (b && b.healthComponent && b.healthComponent.alive && KoopaJrBody.Is(b)) jrs.Add(b);
            return jrs.Count;
        }

        void FixedUpdate()
        {
            if (failed || !Plugin.AssetsReady || PauseManager.isPaused) return;
            if (GatherJrs() == 0) return;
            if (!started) Begin(Plugin.AssetsDir);
            if (!ready) return;
            // Every Mario in play stands in it (and Bowser Jr. paints it).
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                var d = b ? b.GetComponent<MarioDriver>() : null;
                if (d && d.SimHandle != IntPtr.Zero) Native.sms_sim_set_goop(d.SimHandle, world);
            }
            // A client's world is a mirror of the server's.
            Native.sms_goop_set_mirror(world, (byte)(NetworkServer.active ? 0 : 1));
            if (NetworkServer.active)
            {
                ServerStep();
                NetServer();
            }
            else NetClient();
        }

        void ServerStep()
        {
            var owner = jrs[0];
            var ownerFeet = UnityHost.ToSim(owner.footPosition);
            int n = GatherTargets();
            int players = 0;
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                if (!b || !b.GetComponent<MarioDriver>()) continue;
                if (players >= feet.Length) break;
                feet[players++] = UnityHost.ToSim(b.footPosition);
            }
            Native.sms_goop_step(world, ref host, ref ownerFeet, targets, (uint)n, feet, (uint)players, Time.fixedDeltaTime);
            if (HurtEnemies.Value && n > 0)
            {
                uint period = (uint)Mathf.Max(1, Mathf.RoundToInt(ContactPeriod.Value * Native.sms_tick_hz()));
                uint hits = Native.sms_goop_contacts(world, targets, (uint)n, period, contactIds, contactKinds, (uint)contactIds.Length);
                for (int i = 0; i < hits; i++)
                    if (byId.TryGetValue(contactIds[i], out var hc)) GoopHarm(hc, contactKinds[i], owner);
            }
            uint e;
            while ((e = Native.sms_goop_events(world, events, (uint)events.Length)) > 0)
            {
                for (int i = 0; i < e; i++) Handle(events[i], owner);
                if (e < events.Length) break;
            }
        }

        /// The enemies near any Bowser Jr., for the critters to go for and the goop to hurt.
        int GatherTargets()
        {
            byId.Clear();
            int n = 0;
            var team = jrs[0].teamComponent ? jrs[0].teamComponent.teamIndex : TeamIndex.Player;
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                if (n >= MaxTargets) break;
                if (!b || !b.healthComponent || !b.healthComponent.alive || !b.teamComponent) continue;
                if (!TeamManager.IsTeamEnemy(team, b.teamComponent.teamIndex)) continue;
                bool near = false;
                foreach (var j in jrs)
                    if ((j.footPosition - b.footPosition).sqrMagnitude < TargetRange * TargetRange) { near = true; break; }
                if (!near) continue;
                ulong id = 0x1_0000_0000UL | (uint)b.healthComponent.GetInstanceID();
                byId[id] = b.healthComponent;
                float height = Mathf.Max(0.5f, (b.corePosition.y - b.footPosition.y) * 2f);
                targets[n++] = new Native.GoopTarget
                {
                    id = id,
                    pos = UnityHost.ToSim(b.footPosition),
                    radius = Mathf.Max(0.3f, b.radius) / UnityHost.Scale,
                    height = height / UnityHost.Scale,
                };
            }
            return n;
        }

        /// One goop kind's harm to an enemy standing in it (GoopKind as u8).
        static void GoopHarm(HealthComponent hc, byte kind, CharacterBody jr)
        {
            float scale;
            DamageType type = DamageType.Generic;
            switch (kind)
            {
                case 0: scale = 0.6f; Buff(hc, RoR2Content.Buffs.Slow50, 1f); break;       // brown: bogged down
                case 1: case 7: scale = 0.6f; type = DamageType.IgniteOnHit; break;      // fire, scorching
                case 17: scale = 1f; type = DamageType.IgniteOnHit; break;               // lava
                case 2: scale = 0.3f; Buff(hc, RoR2Content.Buffs.Slow50, 0.6f); break;   // oil
                case 3: scale = 0.3f; Buff(hc, RoR2Content.Buffs.Slow80, 0.6f); break;   // glass wall
                case 4: scale = 0.8f; if (UnityEngine.Random.value < 0.25f) type = DamageType.Stun1s; break;
                case 5: scale = 3f; break;                                               // deep: swallowed
                case 16: scale = 0.5f; type = DamageType.PoisonOnHit; break;
                default: return;                                                         // paint: harmless
            }
            if (!jr || !hc || !hc.body) return;
            hc.TakeDamage(new DamageInfo
            {
                attacker = jr.gameObject,
                inflictor = jr.gameObject,
                damage = jr.damage * GoopDamage.Value * scale,
                position = hc.body.footPosition,
                procCoefficient = 0f,
                crit = false,
                damageType = type,
                damageColorIndex = DamageColorIndex.Item,
            });
        }

        static void Buff(HealthComponent hc, BuffDef buff, float seconds)
        {
            if (hc && hc.body && buff) hc.body.AddTimedBuff(buff, seconds);
        }

        void Handle(Native.GoopEvent e, CharacterBody jr)
        {
            var at = UnityHost.ToUnity(e.pos);
            switch (e.kind)
            {
                case Native.GoopEv.Hit:
                    // a: bit 0 a Mario was hit; above it the hit's share in hundredths (0: all of it).
                    if ((e.a & 1) == 0 && byId.TryGetValue(e.id, out var hc) && hc && hc.alive && jr)
                    {
                        int share = e.a >> 8;
                        var di = new DamageInfo
                        {
                            attacker = jr.gameObject,
                            inflictor = jr.gameObject,
                            damage = jr.damage * CritterDamage.Value * (share > 0 ? share / 100f : 1f),
                            position = at,
                            procCoefficient = 0.5f,
                            crit = jr.RollCrit(),
                            damageColorIndex = DamageColorIndex.Default,
                        };
                        hc.TakeDamage(di);
                        GlobalEventManager.instance.OnHitEnemy(di, hc.gameObject);
                        GlobalEventManager.instance.OnHitAll(di, hc.gameObject);
                    }
                    break;
                case Native.GoopEv.Burst:
                    // The swollen Piranha's coins (`TPakkun`'s burst), the server's like every coin.
                    if (jr && jr.teamComponent && MarioCoins.Instance)
                    {
                        int coins = Mathf.Clamp(e.a, 1, 10);
                        for (int c = 0; c < coins; c++)
                        {
                            float a = c * Mathf.PI * 2f / coins;
                            MarioCoins.Instance.Burst(at + Vector3.up,
                                new Native.Vec3(Mathf.Cos(a) * 2f, 10f + c, Mathf.Sin(a) * 2f), jr.teamComponent.teamIndex);
                        }
                    }
                    break;
                case Native.GoopEv.Sound:
                    if (e.name != IntPtr.Zero)
                    {
                        Sound(Name(e.name), e.pos);
                        QueueFx(true, e.name, e.pos, 1f);
                    }
                    break;
                case Native.GoopEv.Effect:
                case Native.GoopEv.Ambient:
                case Native.GoopEv.Erased:
                    if (e.name != IntPtr.Zero)
                    {
                        float scale = e.kind == Native.GoopEv.Erased ? 1f : Mathf.Max(0.1f, e.value);
                        Emit(Name(e.name), e.pos, scale);
                        QueueFx(false, e.name, e.pos, scale);
                    }
                    break;
            }
        }

        // The world's names live as long as it does: read each once.
        byte[] Name(IntPtr p)
        {
            if (names.TryGetValue(p, out var b)) return b;
            var s = Marshal.PtrToStringAnsi(p) ?? "";
            return names[p] = NativeLib.CString(s);
        }

        static void Sound(byte[] name, Native.Vec3 pos)
        {
            var player = MarioView.Player;
            if (player == null || player.Handle == IntPtr.Zero) return;
            Native.sms_view_sound(player.Handle, name, ref pos);
        }

        void Emit(byte[] name, Native.Vec3 pos, float scale)
        {
            if (!EnsureFx()) return;
            Native.sms_view_effect(fxView, name, ref pos, scale);
            fxUntil = Time.time + 4f;
        }

        bool EnsureFx()
        {
            if (fx != null) return true;
            if (fxTried || !Plugin.AssetsReady) return false;
            fxTried = true;
            fxView = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, 8);
            if (fxView != IntPtr.Zero) fx = MarioFx.Create(fxView);
            return fx != null;
        }

        void StepFx()
        {
            if (fx == null || Time.time > fxUntil) return;
            var cam = GameSettings.PlayerCamera();
            var fwd = cam ? cam.forward : Vector3.forward;
            var up = cam ? cam.up : Vector3.up;
            var right = Vector3.Cross(up, fwd).normalized;
            var r = new Native.Vec3(right.x, right.y, right.z);
            var u = new Native.Vec3(up.x, up.y, up.z);
            Native.sms_view_effects_step(fxView, Time.deltaTime, ref r, ref u);
            fx.Update(fxView, UnityHost.Scale, Vector3.zero);
        }

        // ---- drawing ----

        void LateUpdate()
        {
            if (!ready || failed) return;
            StepFx();
            if (!Plugin.DrawModel.Value) return;
            var cam = GameSettings.PlayerCamera();
            var eye = cam ? cam.position : Vector3.zero;
            DrawChunks(eye);
            DrawCritters(eye);
        }

        void DrawChunks(Vector3 eye)
        {
            uint total = Native.sms_goop_chunk_list(world, keys, versions, MaxChunks);
            int n = (int)Math.Min(total, (uint)MaxChunks);
            if (n == 0 && chunks.Count == 0) return;
            foreach (var c in chunks.Values) c.seen = false;
            int uploads = 0;
            float now = Time.time;
            for (int i = 0; i < n; i++)
            {
                var key = (keys[2 * i], keys[2 * i + 1]);
                if (!chunks.TryGetValue(key, out var c))
                {
                    c = MakeChunk(key);
                    if (c == null) return;
                    chunks[key] = c;
                }
                c.seen = true;
                var centre = new Vector3((key.Item1 + 0.5f) * ChunkUnits, 0f, (key.Item2 + 0.5f) * ChunkUnits) * UnityHost.Scale;
                float flat = new Vector2(centre.x - eye.x, centre.z - eye.z).magnitude;
                bool changed = !c.built || c.version != versions[i];
                bool swim = flat < SwimRange && now - c.drawnAt >= SwimEvery;
                if ((!changed && !swim) || uploads >= UploadsPerFrame) continue;
                uploads++;
                if (changed)
                {
                    c.version = versions[i];
                    c.built = true;
                    BuildMesh(c, key);
                }
                if (Native.sms_goop_chunk_image_at(world, key.Item1, key.Item2, ImageRes, Time.time, image) != 0)
                {
                    c.tex.LoadRawTextureData(image);
                    c.tex.Apply(false);
                }
                c.drawnAt = now;
            }
            staleChunks.Clear();
            foreach (var kv in chunks)
                if (!kv.Value.seen) staleChunks.Add(kv.Key);
            foreach (var key in staleChunks) { Free(chunks[key]); chunks.Remove(key); }
        }

        Chunk MakeChunk((int, int) key)
        {
            if (!root)
            {
                root = new GameObject("Bowser Jr. goop");
                DontDestroyOnLoad(root);
            }
            var tex = new Texture2D(ImageRes, ImageRes, TextureFormat.RGBA32, false)
            {
                name = $"goop {key.Item1},{key.Item2}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            // The pollution layers are alpha-tested and lit on the disc (the chunk picture's alpha
            // is the density eased round the kind's threshold): RoR2's lit standard shader with
            // its cutout, a little gloss for the wet paint.
            var shader = Shaders.HgStandard();
            if (!shader)
            {
                Destroy(tex);
                failed = true;
                Plugin.Log.LogWarning("Bowser Jr.'s goop isn't drawn: no standard shader");
                return null;
            }
            var mat = new Material(shader) { name = "Bowser Jr. goop" };
            mat.SetTexture("_MainTex", tex);
            mat.SetColor("_Color", Color.white);
            mat.SetFloat("_EnableCutout", 1f);
            mat.EnableKeyword("CUTOUT");
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.5f);
            mat.SetFloat("_Cull", 0f);
            mat.SetFloat("_RampInfo", 1f);
            mat.SetFloat("_SpecularStrength", 0.35f);
            mat.SetFloat("_SpecularExponent", 18f);
            mat.SetFloat("_Smoothness", 0.4f);
            var go = new GameObject($"goop chunk {key.Item1},{key.Item2}");
            go.transform.SetParent(root.transform, false);
            var mesh = new Mesh { name = go.name };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return new Chunk { go = go, mesh = mesh, tex = tex, mat = mat };
        }

        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<int> tris = new List<int>();
        readonly int[] corner = new int[17 * 17];

        /// A quad over each texel with goop, at its height; shared corners take the mean of the
        /// goop texels round them, so a sloped patch has no steps.
        void BuildMesh(Chunk c, (int, int) key)
        {
            verts.Clear();
            uvs.Clear();
            tris.Clear();
            if (Native.sms_goop_chunk(world, key.Item1, key.Item2, density, kinds, heights) == 0)
            {
                c.mesh.Clear();
                return;
            }
            for (int i = 0; i < corner.Length; i++) corner[i] = -1;
            float x0 = key.Item1 * ChunkUnits, z0 = key.Item2 * ChunkUnits;
            int Corner(int cx, int cz)
            {
                int k = cz * 17 + cx;
                if (corner[k] >= 0) return corner[k];
                float sum = 0f;
                int count = 0;
                for (int dz = -1; dz <= 0; dz++)
                    for (int dx = -1; dx <= 0; dx++)
                    {
                        int tx = cx + dx, tz = cz + dz;
                        if (tx < 0 || tz < 0 || tx >= 16 || tz >= 16) continue;
                        int t = tz * 16 + tx;
                        if (density[t] == 0 || !float.IsFinite(heights[t])) continue;
                        sum += heights[t];
                        count++;
                    }
                float y = (count > 0 ? sum / count : 0f) * UnityHost.Scale;
                var at = new Vector3(x0 + cx * TexelUnits, 0f, z0 + cz * TexelUnits) * UnityHost.Scale;
                // On the stage's own ground (a slope or bump between the sim's probes, and the
                // same height on both sides of a chunk edge, so neighbours meet).
                if (Physics.Raycast(new Vector3(at.x, y + ConformAbove, at.z), Vector3.down, out var hit,
                        ConformAbove + ConformBand, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                    y = hit.point.y;
                corner[k] = verts.Count;
                verts.Add(new Vector3(at.x, y + Lift, at.z));
                uvs.Add(new Vector2(cx / 16f, cz / 16f));
                return corner[k];
            }
            for (int tz = 0; tz < 16; tz++)
                for (int tx = 0; tx < 16; tx++)
                {
                    if (density[tz * 16 + tx] == 0) continue;
                    int a = Corner(tx, tz), b = Corner(tx + 1, tz), d = Corner(tx, tz + 1), e = Corner(tx + 1, tz + 1);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                    tris.Add(b); tris.Add(d); tris.Add(e);
                }
            c.mesh.Clear();
            c.mesh.SetVertices(verts);
            c.mesh.SetUVs(0, uvs);
            c.mesh.SetTriangles(tris, 0);
            c.mesh.RecalculateNormals();
            c.mesh.RecalculateBounds();
        }

        void DrawCritters(Vector3 eye)
        {
            uint total = Native.sms_goop_critters(world, critters, MaxCritters);
            int n = (int)Math.Min(total, (uint)MaxCritters);
            if (n == 0 && props.Count == 0) return;
            float now = Time.time;
            if (shown.Count > 4 * MaxCritters) shown.Clear();
            for (int i = 0; i < n; i++)
            {
                var c = critters[i];
                Smooth(ref c);
                if ((UnityHost.ToUnity(c.pos) - eye).sqrMagnitude > DrawRange * DrawRange) continue;
                if (!props.TryGetValue(c.id, out var prop))
                {
                    if (props.Count >= MaxViews || (prop = Make(c.kind)) == null) continue;
                    props[c.id] = prop;
                }
                prop.seen = now;
                Draw(prop, c);
            }
            // Gone from the world (or out of range): hidden at once, freed after a moment.
            stale.Clear();
            foreach (var kv in props)
            {
                if (kv.Value.seen >= now) continue;
                kv.Value.models?.HideAll();
                kv.Value.brainModels?.HideAll();
                if (now - kv.Value.seen > LingerSeconds) stale.Add(kv.Key);
            }
            foreach (var key in stale) { Free(props[key]); props.Remove(key); }
        }

        Prop Make(byte kind)
        {
            // A goop ball (2) is a TPakkunSeed shot straight: the seed's model (pakkun.cpp loads
            // seed.bmd for both), as are Shadow Mario's brush blobs.
            byte look = kind == 0 ? Native.LookStu : kind == 1 ? Native.LookPiranha : kind == 4 ? Native.LookGateKeeper : Native.LookSeed;
            var view = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, look);
            if (view == IntPtr.Zero) return null;
            var names = kind == 0 ? StuClips : kind == 1 ? PiranhaClips : kind == 4 ? KeeperClips : Array.Empty<string>();
            var prop = new Prop { view = view, clip = new int[names.Length], frames = new float[names.Length] };
            for (int i = 0; i < names.Length; i++)
                prop.clip[i] = Native.sms_view_prop_clip(view, NativeLib.CString(names[i]), out prop.frames[i]);
            prop.source = new MarioFlowers.PropSource(view);
            prop.models = ModelView.Create(prop.source, kind == 0 ? "Swoopin' Stu" : kind == 1 ? "goop Piranha" : kind == 2 ? "goop ball"
                : kind == 4 ? "Gatekeeper" : "Piranha seed");
            if (prop.models == null) { Free(prop); return null; }
            if (kind == 0)
            {
                // The Stu's brain rides on joint 2 (`TSharedParts(.., 2, ..)`).
                prop.brainView = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, Native.LookStuBrain);
                if (prop.brainView != IntPtr.Zero)
                {
                    prop.brainSource = new MarioFlowers.PropSource(prop.brainView);
                    prop.brainModels = ModelView.Create(prop.brainSource, "Swoopin' Stu brain");
                }
            }
            return prop;
        }

        readonly float[] joint = new float[16];

        void Draw(Prop prop, Native.Critter c)
        {
            int clip = -1;
            float frame = 0f;
            if (prop.clip.Length > 0)
            {
                int i = Mathf.Clamp(c.clip, 0, prop.clip.Length - 1);
                clip = prop.clip[i];
                bool loop = c.kind == 0 ? StuLoops[i] : c.kind == 4 ? KeeperLoops[i] : PiranhaLoops[i];
                // Two 120 Hz ticks a 60 fps frame.
                frame = c.clip_time * 0.5f;
                float length = prop.frames[i];
                if (length > 1f) frame = loop ? frame % length : Mathf.Min(frame, length - 1f);
            }
            float degrees = c.yaw * Mathf.Rad2Deg;
            short yaw = (short)Mathf.RoundToInt(degrees / 180f * 32768f);
            Native.sms_view_prop_update(prop.view, yaw, clip, frame, 1);
            var rot = Quaternion.Euler(0f, degrees, 0f) * Quaternion.Euler(c.lean * Mathf.Rad2Deg, 0f, 0f);
            prop.source.draw = new ViewDraw
            {
                key = 1, model = 0, pos = new ViewVec3 { x = c.pos.x, y = c.pos.y, z = c.pos.z },
                rot_x = rot.x, rot_y = rot.y, rot_z = rot.z, rot_w = rot.w, alpha = 1f,
            };
            // ModelView places a draw at pos * scale, so a grown or shrunk critter is placed here
            // (its own size only scales the model).
            float scale = UnityHost.Scale * Mathf.Max(0.05f, c.scale);
            var at = UnityHost.ToUnity(c.pos);
            prop.models.Update(scale, _ => (at, rot));
            if (prop.brainModels == null) return;
            if (Native.sms_view_prop_joint(prop.view, clip, frame, 2, joint) == 0) { prop.brainModels.HideAll(); return; }
            var m = new Matrix4x4();
            for (int k = 0; k < 16; k++) m[k] = joint[k];
            var offset = rot * (new Vector3(m.m03, m.m13, m.m23) * Mathf.Max(0.05f, c.scale));
            var brainRot = rot * m.rotation;
            prop.brainSource.draw = new ViewDraw
            {
                key = 1, model = 0,
                pos = new ViewVec3 { x = c.pos.x + offset.x, y = c.pos.y + offset.y, z = c.pos.z + offset.z },
                rot_x = brainRot.x, rot_y = brainRot.y, rot_z = brainRot.z, rot_w = brainRot.w, alpha = 1f,
            };
            Native.sms_view_prop_update(prop.brainView, yaw, -1, 0f, 1);
            var brainAt = at + offset * UnityHost.Scale;
            prop.brainModels.Update(scale, _ => (brainAt, brainRot));
        }
    }
}
