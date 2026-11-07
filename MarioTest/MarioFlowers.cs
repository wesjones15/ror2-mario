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
    // While a tower or head is under the ground, a plain flower stands where it is
    // (`THanaSambo::initFlower` / `TSamboHead::initFlower`, kind 3): the tower leaves it as it
    // sinks and the head grows one when it hides, and it goes as the enemy bursts out through
    // it (`TNerveHanaSamboAppear` / `TNerveSamboHeadAppear`: `unk1A8->hide()`). Spraying it
    // opens it (`TSamboFlower::bloom`, pollen only: no coin unit, no chime).
    //
    // Multiplayer: the server owns the one world (placement, the step, bites and coins). Every
    // client holds a mirror of it, refreshed by snapshots of the things near the players, and
    // draws it, so a player who is not Mario sees the flowers, towers and heads too. Only a Mario
    // can wake them, though: his sim (on his own machine) lands the spray and the stomps on the
    // things it knows (the mirror's targets) and reports them, and the server applies them; the
    // enemies chase and bite the nearest Mario only. Models, clips and tuning come from the user's
    // disc (assets/enemy/sambo*, params_enemy/*.prm). Each thing on screen has its own native prop
    // view (one posed instance per view), made when it comes near and freed when it leaves.
    internal sealed class MarioFlowers : MonoBehaviour
    {
        // The three kinds (Native.FlowerItem.kind) and the look codes of their views. Kind 3 is a
        // buried tower's or head's stand-in flower (`THanaSambo::initFlower`): the same plain
        // flower model, so the same look and clips as kind 0.
        const byte Flower = 0, Tower = 1, Head = 2, StandIn = 3;
        // The snapshot's handle field is 16 bits, but a stand-in's sim handle is 0x10000 + tower or
        // 0x20000 + head. Send it as a plain flower (kind 0) with bit 15 set and bit 14 picking head
        // from tower, the index in the low 14 bits. A client from before stand-ins reads it as a
        // flower of an impossible unit (>= 512) and its mirror drops it, so the wire stays compatible;
        // a current client turns it back into kind 3. Real flowers and enemies never reach bit 15.
        const ushort StandInWire = 0x8000, StandInHeadWire = 0x4000, StandInMask = 0x3FFF;
        static readonly byte[] Looks = { 5, 6, 7, 5 };
        const int MaxViews = 24, MaxItems = 512;
        const float DrawRange = 90f, SnapRange = 130f, MinSpawnDistance = 18f, LingerSeconds = 3f;
        const float SnapEvery = 0.125f, HitResend = 0.3f;

        // Clips by kind, in the order the pose code names them.
        static readonly string[][] ClipNames =
        {
            new[] { "flower_wait", "flower_hit", "flower_fwait" },
            new[] { "sambo_fset", "sambo_fwait", "sambo_fattack_start", "sambo_fattack", "sambo_fattack_end",
                    "sambo_fhide", "sambo_hit", "sambo_down" },
            new[] { "sambohead_set", "sambohead_wait", "sambohead_jump_start", "sambohead_jump_end",
                    "sambohead_land", "sambohead_hit", "sambohead_down" },
            new[] { "flower_wait", "flower_hit", "flower_fwait" },
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

        internal static MarioFlowers Instance { get; private set; }

        // One world for the process: sims hold its pointer, so it is never freed.
        static IntPtr world;

        readonly Dictionary<(byte, uint), Prop> props = new Dictionary<(byte, uint), Prop>();
        readonly List<(byte, uint)> stale = new List<(byte, uint)>();
        readonly List<(uint spawn, Vector3 pos, float yaw)> placements = new List<(uint, Vector3, float)>();
        readonly Native.FlowerEvent[] events = new Native.FlowerEvent[64];
        readonly Native.FlowerHit[] hits = new Native.FlowerHit[64];
        readonly Native.FlowerItem[] items = new Native.FlowerItem[MaxItems];
        readonly Dictionary<(byte, uint), float> hitSent = new Dictionary<(byte, uint), float>();
        // A client's smoothing between snapshots: where each thing was and when the last one came.
        readonly Dictionary<(byte, uint), Vector3> previous = new Dictionary<(byte, uint), Vector3>();
        readonly List<CharacterBody> marios = new List<CharacterBody>();
        Native.Vec3[] feet = new Native.Vec3[16];
        Native.FlowerClips clips;
        bool clipsRead, placed, failed;
        float arrived, snapAt, sentAt;
        // The disc's particle effects (an effects view, look 8) and the mesh that draws them.
        IntPtr fxView;
        MarioFx fx;
        bool fxTried;
        float fxUntil;
        static Native.Host host;
        static bool hostMade;

        void Awake()
        {
            Instance = this;
            SceneManager.activeSceneChanged += SceneChanged;
            Run.onRunDestroyGlobal += RunEnded;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            SceneManager.activeSceneChanged -= SceneChanged;
            Run.onRunDestroyGlobal -= RunEnded;
            foreach (var p in props.Values) Free(p);
            props.Clear();
            fx?.Dispose();
            fx = null;
            if (fxView != IntPtr.Zero) Native.sms_view_free(fxView);
            fxView = IntPtr.Zero;
        }

        void SceneChanged(Scene from, Scene to) => Reset();
        void RunEnded(Run run) => Reset();

        void Reset()
        {
            foreach (var p in props.Values) Free(p);
            props.Clear();
            placements.Clear();
            previous.Clear();
            hitSent.Clear();
            placed = false;
            arrived = 0f;
            if (world != IntPtr.Zero)
            {
                // Empty it: an empty snapshot is a mirror of nothing; the server's setup clears it too.
                Native.sms_fw_mirror(world, items, 0);
                Native.sms_fw_setup(world, NativeLib.CString(Plugin.AssetsDir), ref clips);
            }
        }

        static void Free(Prop p)
        {
            p.models?.Dispose();
            if (p.view != IntPtr.Zero) Native.sms_view_free(p.view);
            p.view = IntPtr.Zero;
        }

        bool EnsureWorld()
        {
            if (failed || !Plugin.AssetsReady) return false;
            if (world == IntPtr.Zero)
            {
                world = Native.sms_fw_new();
                if (world == IntPtr.Zero) { failed = true; return false; }
                if (!hostMade) { host = UnityHost.Table(); hostMade = true; }
            }
            if (!clipsRead) ReadClips();
            return true;
        }

        /// Clip lengths in ticks, read once from the models' own clips. The disc's clips are
        /// authored at 60 frames a second (`SMSGetAnmFrameRate`) and the sim is 120 Hz, so a clip
        /// is two ticks a frame.
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
            Native.sms_fw_setup(world, NativeLib.CString(Plugin.AssetsDir), ref clips);
        }

        static bool Suitable()
        {
            var info = SceneInfo.instance;
            return Run.instance && Stage.instance && info && info.groundNodes && info.sceneDef
                && info.sceneDef.sceneType == SceneType.Stage;
        }

        /// Every Mario body in play, alive, and their feet in sim units.
        int GatherMarios()
        {
            marios.Clear();
            foreach (var b in CharacterBody.readOnlyInstancesList)
                if (b && b.healthComponent && b.healthComponent.alive && b.GetComponent<MarioDriver>()) marios.Add(b);
            if (feet.Length < marios.Count) feet = new Native.Vec3[marios.Count * 2];
            for (int i = 0; i < marios.Count; i++) feet[i] = UnityHost.ToSim(marios[i].footPosition);
            return marios.Count;
        }

        void FixedUpdate()
        {
            if (failed || PauseManager.isPaused || !Plugin.AssetsReady || !EnsureWorld()) return;
            // Each Mario this machine plays hits the world's things with his water and his feet.
            var own = MarioDriver.Player;
            if (own != null && own.SimHandle != IntPtr.Zero) Native.sms_sim_set_flowers(own.SimHandle, world);
            if (NetworkServer.active) ServerStep(); else ClientSendHits();
        }

        // ---- the server ----

        void ServerStep()
        {
            if (Plugin.FlowerChance.Value <= 0 || !Suitable()) return;
            int count = GatherMarios();
            if (count == 0) return;
            if (arrived == 0f) arrived = Time.time;
            if (Time.time - arrived < 2f) return;
            if (!placed)
            {
                placed = true;
                Place();
            }
            Native.sms_fw_step(world, ref host, feet, (uint)count, Time.fixedDeltaTime);
            // Hits the host's own Mario's sim reported, then the clients' (OnHit applies as they come).
            uint n;
            while ((n = Native.sms_fw_take_hits(world, hits, (uint)hits.Length)) > 0)
            {
                for (int i = 0; i < n; i++) Native.sms_fw_hit(world, hits[i].id, hits[i].kind);
                if (n < hits.Length) break;
            }
            while ((n = Native.sms_fw_events(world, events, (uint)events.Length)) > 0)
            {
                for (int i = 0; i < n; i++) Handle(events[i]);
                if (n < events.Length) break;
            }
            if (Mp.Networked && Time.time - sentAt >= SnapEvery)
            {
                sentAt = Time.time;
                SendSnapshot();
            }
        }

        /// The stage's rolls: each may put one thing on a ground node away from every Mario.
        void Place()
        {
            Native.sms_fw_setup(world, NativeLib.CString(Plugin.AssetsDir), ref clips);
            Native.sms_fw_set_table(world, (uint)Plugin.FlowerChance.Value, 3, 5, 2, 2);
            var graph = SceneInfo.instance.groundNodes;
            var nodes = graph.GetActiveNodesForHullMask(HullMask.Human);
            if (nodes == null || nodes.Count == 0) return;
            int made = 0;
            for (int roll = 0; roll < Plugin.FlowerRolls.Value; roll++)
            {
                uint spawn = Native.sms_fw_roll(world, UnityEngine.Random.value, UnityEngine.Random.value);
                if (spawn == 0) continue;
                for (int tries = 0; tries < 40; tries++)
                {
                    if (!graph.GetNodePosition(nodes[UnityEngine.Random.Range(0, nodes.Count)], out Vector3 pos)) continue;
                    if (NearAMario(pos)) continue;
                    float yaw = UnityEngine.Random.Range(0f, 360f);
                    var at = UnityHost.ToSim(pos);
                    if (Native.sms_fw_place(world, spawn, ref at, yaw) == 0) break;
                    placements.Add((spawn, pos, yaw));
                    made++;
                    break;
                }
            }
            Plugin.Log.LogInfo($"Sunshine flowers: {made} set(s) placed on this stage ({Plugin.FlowerRolls.Value} rolls at "
                + $"{Plugin.FlowerChance.Value}/1000)");
        }

        bool NearAMario(Vector3 pos)
        {
            foreach (var m in marios)
                if ((m.footPosition - pos).sqrMagnitude < MinSpawnDistance * MinSpawnDistance) return true;
            return false;
        }

        /// What the world did since the last step: coins, bites, deaths, sounds and bursts.
        void Handle(Native.FlowerEvent e)
        {
            var at = new Vector3(e.pos.x, e.pos.y, e.pos.z) * UnityHost.Scale;
            CharacterBody target = e.target >= 0 && e.target < marios.Count ? marios[e.target] : null;
            switch (e.kind)
            {
                case 3:
                    // The ring's coins are the server's, like every coin (the clients draw copies).
                    if (target && target.teamComponent && MarioCoins.Instance)
                        MarioCoins.Instance.Burst(at, e.vel, target.teamComponent.teamIndex);
                    break;
                case 6:
                    if (target && target.healthComponent && target.healthComponent.alive)
                    {
                        target.healthComponent.TakeDamage(new DamageInfo
                        {
                            damage = target.maxHealth * Plugin.FlowerBiteMax.Value / 100f,
                            position = target.corePosition,
                            procCoefficient = 0f,
                            damageColorIndex = DamageColorIndex.Default,
                        });
                        // His sim is on his machine: the owner knocks him over.
                        if (target.hasEffectiveAuthority) target.GetComponent<MarioDriver>()?.KnockFrom(at);
                        else MarioNet.SendKnock(target, at);
                    }
                    break;
                case 7:
                    // genRandomItem: a handful of coins from where it fell.
                    var who = target ?? (marios.Count > 0 ? marios[0] : null);
                    if (who && who.teamComponent && MarioCoins.Instance)
                    {
                        int coins = e.a == 1 ? 5 : 2;
                        for (int c = 0; c < coins; c++)
                        {
                            float a = c * Mathf.PI * 2f / coins;
                            MarioCoins.Instance.Burst(at + Vector3.up * 1f,
                                new Native.Vec3(Mathf.Cos(a) * 2f, 10f + c, Mathf.Sin(a) * 2f), who.teamComponent.teamIndex);
                        }
                    }
                    break;
            }
            // Sounds and bursts: every machine plays its own.
            Fx(e.kind, e.a, e.pos);
            SendFx(e);
        }

        // ---- messages ----

        void SendSnapshot()
        {
            uint total = Native.sms_fw_items(world, items, (uint)items.Length);
            int n = (int)Math.Min(total, (uint)items.Length);
            if (n == 0) return;
            GatherMarios();
            // Players near enough to see a thing: every Mario, and every player body.
            var eyes = new List<Vector3>();
            foreach (var b in CharacterBody.readOnlyInstancesList)
                if (b && b.isPlayerControlled) eyes.Add(b.footPosition);
            var near = new bool[n];
            // A unit goes whole or not at all (a client's mirror fills gaps in a unit with copies).
            var unitNear = new Dictionary<uint, bool>();
            for (int i = 0; i < n; i++)
            {
                var p = new Vector3(items[i].pos.x, items[i].pos.y, items[i].pos.z) * UnityHost.Scale;
                bool close = false;
                foreach (var e in eyes)
                    if ((e - p).sqrMagnitude < SnapRange * SnapRange) { close = true; break; }
                if (items[i].kind == Flower)
                {
                    uint unit = items[i].handle / 64;
                    unitNear.TryGetValue(unit, out bool was);
                    unitNear[unit] = was || close;
                }
                else near[i] = close;
            }
            var w = new ByteWriter(16 + n * 26);
            int count = 0;
            w.U8(0);
            for (int i = 0; i < n; i++)
            {
                if (items[i].kind == Flower ? !unitNear[items[i].handle / 64] : !near[i]) continue;
                var it = items[i];
                byte kind = it.kind;
                ushort handle = (ushort)it.handle;
                if (kind == StandIn)
                {
                    // 0x10000 + tower or 0x20000 + head, packed into the 16-bit handle field.
                    int packed = StandInWire | (it.handle >= 0x20000u ? StandInHeadWire : 0)
                        | (int)(it.handle & StandInMask);
                    handle = (ushort)packed;
                    kind = Flower;
                }
                w.U8(kind);
                w.U8(it.state);
                w.U16(handle);
                w.U32(it.age);
                w.F32(it.pos.x); w.F32(it.pos.y); w.F32(it.pos.z);
                w.F32(it.yaw);
                count++;
            }
            if (count == 0) return;
            var bytes = w.ToArray();
            bytes[0] = (byte)Math.Min(count, 255);
            Mp.SendToAll(MarioNet.FlowerSnapMsg, bytes);
        }

        /// A client: the server's snapshot becomes the mirror, remembering where things were.
        internal static void OnSnapshot(ByteReader r)
        {
            var self = Instance;
            if (!self || !self.EnsureWorld()) return;
            int count = r.U8();
            var list = new Native.FlowerItem[Math.Min(count, MaxItems)];
            for (int i = 0; i < list.Length; i++)
            {
                byte kind = r.U8();
                byte state = r.U8();
                ushort raw = r.U16();
                uint handle = raw;
                if (kind == Flower && (raw & StandInWire) != 0)
                {
                    // A stand-in packed into a flower's slot (SendSnapshot): no real flower has a
                    // unit this high, so unpack it back into a kind-3 item on the tower or head.
                    kind = StandIn;
                    handle = ((raw & StandInHeadWire) != 0 ? 0x20000u : 0x10000u) | (uint)(raw & StandInMask);
                }
                list[i] = new Native.FlowerItem
                {
                    kind = kind,
                    state = state,
                    handle = handle,
                    age = r.U32(),
                    pos = new Native.Vec3(r.F32(), r.F32(), r.F32()),
                    yaw = r.F32(),
                };
            }
            if (r.Failed) return;
            uint had = Native.sms_fw_items(world, self.items, (uint)self.items.Length);
            self.previous.Clear();
            for (int i = 0; i < Math.Min(had, (uint)self.items.Length); i++)
                self.previous[(self.items[i].kind, self.items[i].handle)] =
                    new Vector3(self.items[i].pos.x, self.items[i].pos.y, self.items[i].pos.z);
            Native.sms_fw_mirror(world, list, (uint)list.Length);
            self.snapAt = Time.time;
        }

        void SendFx(Native.FlowerEvent e)
        {
            if (!Mp.Networked) return;
            var w = new ByteWriter(24);
            w.U8((byte)e.kind);
            w.U32((uint)e.a);
            w.F32(e.pos.x); w.F32(e.pos.y); w.F32(e.pos.z);
            Mp.SendToAll(MarioNet.FlowerFxMsg, w.ToArray());
        }

        internal static void OnFx(ByteReader r)
        {
            uint kind = r.U8();
            int a = (int)r.U32();
            var pos = new Native.Vec3(r.F32(), r.F32(), r.F32());
            if (r.Failed || !float.IsFinite(pos.x + pos.y + pos.z)) return;
            Fx(kind, a, pos);
        }

        /// A client's Mario sprayed or stomped a flower thing: the server applies it.
        void ClientSendHits()
        {
            uint n = Native.sms_fw_take_hits(world, hits, (uint)hits.Length);
            for (int i = 0; i < n; i++)
            {
                var key = ((byte)hits[i].kind, (uint)(hits[i].id & 0xFFFFFFFF));
                if (hitSent.TryGetValue(key, out float last) && Time.time - last < HitResend) continue;
                hitSent[key] = Time.time;
                var w = new ByteWriter(8);
                w.U32((uint)(hits[i].id - IdBase));
                w.U8((byte)hits[i].kind);
                Mp.SendToServer(MarioNet.FlowerHitMsg, w.ToArray());
            }
        }

        const ulong IdBase = 0x4000_0000UL;

        internal static void OnHit(ByteReader r)
        {
            uint low = r.U32();
            byte kind = r.U8();
            if (r.Failed || world == IntPtr.Zero || low >= 0x30000) return;
            Native.sms_fw_hit(world, IdBase + low, kind);
        }

        /// A sound and a burst of particles at a sim position, on this machine (the disc's:
        /// `TSamboFlower::bloom`, `TNerveHanaSamboAppear/Hide/Die`, `THanaSambo::createPollen`).
        static void Fx(uint kind, int a, Native.Vec3 pos)
        {
            switch (kind)
            {
                case 1:
                    // `a` -1: the stand-in flower a buried enemy leaves, which the game opens
                    // with pollen only (`bloom`: `unk164` null, so no open chime).
                    if (a >= 0) Sound("MSD_SE_OBJ_FLOWER_OPEN_" + Mathf.Clamp(a, 0, 11), pos);
                    Emit("ms_smb_kafun_ow", pos);
                    break;
                case 3: Sound("MSD_SE_SY_COIN_APPEAR", pos); break;
                case 4:
                    Sound("MSD_SE_EN_SAMBO_APPEAR", pos);
                    Emit("ms_smb_ap_rock", pos);
                    Emit("ms_smb_ap_smoke", pos);
                    break;
                case 5:
                    Emit("ms_smb_hd_rock", pos);
                    Emit("ms_smb_hd_smoke", pos);
                    break;
                case 6:
                    // The pollen the biting head throws, where the head is.
                    Sound("MSD_SE_EN_SAMBO_ATTACK", pos);
                    Emit("ms_smb_kafun_oa", pos);
                    break;
                case 7:
                    Sound("MSD_SE_SY_COIN_APPEAR", pos);
                    // `TNerveHanaSamboDie`: a puff at four joints up the body.
                    for (int i = 0; i < 4; i++)
                    {
                        var at = new Native.Vec3(pos.x, pos.y + 40f + i * (a == 1 ? 90f : 30f), pos.z);
                        Emit("ms_enm_disap_a", at);
                        Emit("ms_enm_disap_b", at);
                    }
                    break;
            }
        }

        static void Emit(string name, Native.Vec3 pos)
        {
            var self = Instance;
            if (!self || !self.EnsureFx()) return;
            Native.sms_view_effect(self.fxView, NativeLib.CString(name), ref pos, 1f);
            self.fxUntil = Time.time + 4f;
        }

        bool EnsureFx()
        {
            if (fx != null) return true;
            if (fxTried || !Plugin.AssetsReady) return false;
            fxTried = true;
            fxView = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, 8);
            if (fxView != IntPtr.Zero) fx = MarioFx.Create(fxView);
            if (fx == null) Plugin.Log.LogInfo("The flowers' particle effects aren't drawn (no effects in the assets folder).");
            return fx != null;
        }

        /// Runs and draws the effects while any are alive.
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

        static void Sound(string name, Native.Vec3 pos)
        {
            var player = MarioView.Player;
            if (player == null || player.Handle == IntPtr.Zero) return;
            Native.sms_view_sound(player.Handle, NativeLib.CString(name), ref pos);
        }

        // ---- drawing (every machine, any player) ----

        void LateUpdate()
        {
            if (world == IntPtr.Zero || failed) return;
            StepFx();
            uint total = Native.sms_fw_items(world, items, (uint)items.Length);
            int n = (int)Math.Min(total, (uint)items.Length);
            if (n == 0 && props.Count == 0) return;
            var cam = GameSettings.PlayerCamera();
            var eye = cam ? cam.position : Vector3.zero;
            float now = Time.time;
            bool mirrored = !NetworkServer.active;
            float since = mirrored ? Mathf.Max(0f, now - snapAt) : 0f;
            float blend = mirrored ? Mathf.Clamp01(since / SnapEvery) : 1f;
            for (int i = 0; i < n; i++)
            {
                var item = items[i];
                if (!Visible(item.kind, item.state)) continue;
                var key = (item.kind, item.handle);
                var at = new Vector3(item.pos.x, item.pos.y, item.pos.z);
                if (mirrored && blend < 1f && previous.TryGetValue(key, out var was)) at = Vector3.Lerp(was, at, blend);
                if ((at * UnityHost.Scale - eye).sqrMagnitude > DrawRange * DrawRange) continue;
                item.pos = new Native.Vec3(at.x, at.y, at.z);
                item.age += (uint)(since * 120f);
                if (!props.TryGetValue(key, out var prop))
                {
                    if (props.Count >= MaxViews || (prop = Make(item.kind)) == null) continue;
                    props[key] = prop;
                }
                prop.seen = now;
                Draw(prop, item.kind, item);
            }
            stale.Clear();
            foreach (var kv in props)
                if (now - kv.Value.seen > LingerSeconds) stale.Add(kv.Key);
            foreach (var key in stale) { Free(props[key]); props.Remove(key); }
        }

        /// Hidden towers and sleeping heads are not drawn (under the ground / not there yet);
        /// the stand-in flower a buried one leaves there is (`THanaSambo::initFlower`).
        static bool Visible(byte kind, byte state) =>
            kind == Flower || kind == StandIn
            || (kind == Tower ? state != 0 && state != 9 : state != 0 && state != 4);

        Prop Make(byte kind)
        {
            var view = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, Looks[kind]);
            if (view == IntPtr.Zero)
            {
                failed = true;
                Plugin.Log.LogInfo("The Sunshine flower models aren't in the assets folder (they are fetched from the " +
                    "stage archives on the first run after an update: put your Sunshine disc in wesleyasaurus-SharedFiles/put_games_here " +
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
                case StandIn: // the same bloom phases: a buried enemy's flower
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
        // Animated textures pass through (the Gatekeeper's scrolling goop); other props' stay 0.
        internal sealed class PropSource : IViewSource, IAnimatedTextureSource
        {
            public uint TextureVersion(uint id) => native.TextureVersion(id);
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
