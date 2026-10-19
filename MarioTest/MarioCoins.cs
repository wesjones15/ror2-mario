using System;
using Ror2Common;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace MarioTest
{
    // Host-owned drops for kills credited to a Mario body, including his item procs.
    // The yellow mesh/materials, pickup sound and sparks come from the user's Sunshine disc.
    // TCoin::taken / TMario::getCoin restore one of eight life segments. RoR2 owns healing,
    // so it goes through HealthComponent.Heal and normal healing-item rules.
    internal sealed class MarioCoins : MonoBehaviour
    {
        internal const int Capacity = 128;
        const float Lifetime = 60f, PickupDelay = 0.3f;
        sealed class Coin
        {
            public Vector3 pos, velocity;
            public TeamIndex team;
            public float age;
            public bool settled;
            /// When a client last told the server one of its Marios touched this coin.
            public float touchSent = -10f;
        }

        readonly Coin[] coins = new Coin[Capacity];
        readonly System.Collections.Generic.List<CharacterBody> collectors = new System.Collections.Generic.List<CharacterBody>();
        readonly System.Collections.Generic.List<CharacterBody> ownMarios = new System.Collections.Generic.List<CharacterBody>();
        readonly System.Collections.Generic.HashSet<int> rewarded = new System.Collections.Generic.HashSet<int>();
        CoinSource source;
        ModelView models;
        IntPtr view;
        int next;
        bool tried;

        /// The plugin's coins (for the messages that mirror them to the other players).
        internal static MarioCoins Instance { get; private set; }

        void Awake()
        {
            Instance = this;
            GlobalEventManager.onDeathProcServer += Killed;
            SceneManager.activeSceneChanged += SceneChanged;
            Run.onRunDestroyGlobal += RunEnded;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            GlobalEventManager.onDeathProcServer -= Killed;
            SceneManager.activeSceneChanged -= SceneChanged;
            Run.onRunDestroyGlobal -= RunEnded;
            Clear();
            if (view != IntPtr.Zero) Native.sms_view_free(view);
            view = IntPtr.Zero;
        }

        void SceneChanged(Scene from, Scene to) => Clear();
        void RunEnded(Run run) => Clear();

        void Clear()
        {
            Array.Clear(coins, 0, coins.Length);
            rewarded.Clear();
            next = 0;
            collectors.Clear();
            models?.Dispose();
            models = null;
            if (source != null) source.count = 0;
        }

        bool EnsureModel()
        {
            if (!Plugin.AssetsReady) return false;
            if (view == IntPtr.Zero && !tried)
            {
                tried = true;
                view = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, 2);
                if (view == IntPtr.Zero)
                    Plugin.Log.LogError("Yellow coin model did not load: need scene/mapobj/coin.bmd extracted from your Sunshine ISO.");
                else
                    source = new CoinSource(view);
            }
            if (view == IntPtr.Zero) return false;
            if (models == null) models = ModelView.Create(source, "Sunshine yellow coins");
            return models != null;
        }

        void Killed(DamageReport report)
        {
            if (!NetworkServer.active || !Run.instance || report == null || !report.attackerBody
                || !report.victimBody || !report.victim || !report.attackerBody.GetComponent<MarioDriver>()) return;
            var attacker = report.attackerBody;
            var victim = report.victimBody;
            if (!attacker.teamComponent || !victim.teamComponent
                || !TeamMask.GetEnemyTeams(attacker.teamComponent.teamIndex).HasTeam(victim.teamComponent.teamIndex)) return;
            if (!rewarded.Add(victim.GetInstanceID()) || !EnsureModel()) return;
            float unit = UnityHost.Scale;
            var coin = new Coin
            {
                pos = victim.corePosition,
                velocity = UnityEngine.Random.insideUnitSphere * (80f * unit) + Vector3.up * (220f * unit),
                team = attacker.teamComponent.teamIndex,
            };
            coins[next] = coin;
            MarioNet.SendCoin(next, coin.pos, coin.velocity, coin.team);
            next = (next + 1) % Capacity;
        }

        /// The server made a coin: every other machine drops the same one (it lands where its own world puts it;
        /// only the server picks coins up).
        internal void Spawned(int index, Vector3 pos, Vector3 velocity, TeamIndex team)
        {
            if (index < 0 || index >= Capacity || !EnsureModel()) return;
            coins[index] = new Coin { pos = pos, velocity = velocity, team = team };
        }

        /// The server says a Mario picked this coin up.
        internal void Taken(int index)
        {
            if (index >= 0 && index < Capacity) coins[index] = null;
        }

        void FixedUpdate()
        {
            if (!Run.instance || PauseManager.isPaused) return;
            using var timing = MarioPerformance.Measure(MarioPerformance.Part.Coins);
            float dt = Time.fixedDeltaTime;
            float unit = UnityHost.Scale;
            bool gathered = false, ownGathered = false;
            for (int i = 0; i < coins.Length; i++)
            {
                var coin = coins[i];
                if (coin == null) continue;
                coin.age += dt;
                if (coin.age >= Lifetime) { coins[i] = null; continue; }
                if (!coin.settled)
                {
                    coin.velocity += Physics.gravity * dt;
                    var step = coin.velocity * dt;
                    float distance = step.magnitude;
                    float radius = 10f * unit;
                    if (distance > 0f && Physics.SphereCast(coin.pos, radius, step / distance,
                        out var hit, distance, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                    {
                        coin.pos = hit.point + hit.normal * (radius + unit);
                        coin.velocity = Vector3.ProjectOnPlane(coin.velocity, hit.normal) * 0.3f;
                        coin.settled = hit.normal.y > 0.5f;
                    }
                    else coin.pos += step;
                }
                // Only the server picks coins up; the others let them fall and spin, and tell it when
                // one of their own Marios touches one (the server's copy of that body may lag).
                if (coin.age < PickupDelay) continue;
                if (!NetworkServer.active)
                {
                    if (!ownGathered)
                    {
                        ownGathered = true;
                        ownMarios.Clear();
                        // Only bodies this machine drives: usually one, and none to look at otherwise.
                        foreach (var candidate in CharacterBody.readOnlyInstancesList)
                            if (candidate && candidate.hasEffectiveAuthority && candidate.GetComponent<MarioDriver>()) ownMarios.Add(candidate);
                    }
                    TouchedByOwnMario(i, coin);
                    continue;
                }
                if (!gathered)
                {
                    gathered = true;
                    collectors.Clear();
                    // Preserve CharacterBody order (including AI/cloned Marios), but scan the
                    // stage once, instead of once per coin. No scan on clients or without coins.
                    foreach (var candidate in CharacterBody.readOnlyInstancesList)
                        if (candidate && candidate.GetComponent<MarioDriver>()) collectors.Add(candidate);
                }
                foreach (var body in collectors)
                {
                    if (!body || !body.healthComponent || !body.healthComponent.alive || !body.teamComponent
                        || body.teamComponent.teamIndex != coin.team || !body.GetComponent<MarioDriver>()) continue;
                    var feet = body.footPosition;
                    var delta = coin.pos - feet;
                    // coin_hit_data_table: radius 70, height 100; Mario's cylinder adds 50/160.
                    if (delta.x * delta.x + delta.z * delta.z > 120f * 120f * unit * unit
                        || coin.pos.y + 100f * unit < feet.y || coin.pos.y > feet.y + 160f * unit) continue;
                    Collect(i, body);
                    break;
                }
            }
        }

        /// Whether a Mario's cylinder reaches a coin (coin_hit_data_table: radius 70, height 100; Mario's
        /// cylinder adds 50 and 160).
        static bool Reaches(Vector3 feet, Vector3 coin)
        {
            float unit = UnityHost.Scale;
            var delta = coin - feet;
            return delta.x * delta.x + delta.z * delta.z <= 120f * 120f * unit * unit
                && coin.y + 100f * unit >= feet.y && coin.y <= feet.y + 160f * unit;
        }

        /// A coin is picked up: it heals once, pays RoR2's gold, and everyone is told. Server only.
        void Collect(int index, CharacterBody body)
        {
            var coin = coins[index];
            if (coin == null) return;
            // Remove first: healing can trigger item callbacks, but a coin heals once.
            coins[index] = null;
            body.healthComponent.Heal(body.healthComponent.fullCombinedHealth / 8f, default, true);
            // And RoR2's own coin: the gold that the yellow counter shows.
            if (body.master && Plugin.CoinGold.Value > 0) body.master.GiveMoney((uint)Plugin.CoinGold.Value, false);
            MarioNet.CoinCollected(body, coin.pos);
            MarioNet.SendCoinTaken(index, body, coin.pos);
        }

        /// A client's own Mario stands on a coin its copy of the world has: ask the server for it. Resent
        /// every half second until the server's "taken" removes the coin.
        void TouchedByOwnMario(int index, Coin coin)
        {
            if (Time.time - coin.touchSent < 0.5f) return;
            foreach (var body in ownMarios)
            {
                if (!body || !body.healthComponent || !body.healthComponent.alive || body.netId.Value == 0) continue;
                if (!Reaches(body.footPosition, coin.pos)) continue;
                coin.touchSent = Time.time;
                MarioNet.SendCoinTouch(index, body);
                return;
            }
        }

        /// The server hears a client's Mario touched coin `index`: any Mario takes any Mario's coin of his team,
        /// wherever the server thinks his body is.
        internal void Touched(int index, CharacterBody body)
        {
            if (!NetworkServer.active || index < 0 || index >= Capacity) return;
            var coin = coins[index];
            if (coin == null || coin.age < PickupDelay || !body || !body.healthComponent || !body.healthComponent.alive
                || !body.teamComponent || body.teamComponent.teamIndex != coin.team || !body.GetComponent<MarioDriver>()) return;
            Collect(index, body);
        }

        void LateUpdate()
        {
            if (models == null || source == null) return;
            source.count = 0;
            // TItemManager::perform rotates coins two degrees per 120 Hz tick.
            var turn = Quaternion.Euler(0f, Time.time * 240f, 0f);
            for (int i = 0; i < coins.Length; i++)
            {
                var coin = coins[i];
                if (coin == null) continue;
                var pos = UnityHost.ToSim(coin.pos);
                source.draws[source.count++] = new ViewDraw
                {
                    key = (ulong)i + 1, model = 0, pos = new ViewVec3 { x = pos.x, y = pos.y, z = pos.z },
                    rot_x = turn.x, rot_y = turn.y, rot_z = turn.z, rot_w = turn.w, alpha = 1f,
                };
            }
            models.Update(UnityHost.Scale);
        }

        // One shared native bind pose and material cache, many host-owned instances.
        // The native coin view is never stepped: every coin reuses its bind geometry.
        internal sealed class CoinSource : IViewSource, IStaticGeometrySource
        {
            readonly SmsViewSource native;
            public readonly ViewDraw[] draws = new ViewDraw[Capacity];
            public int count;
            public CoinSource(IntPtr view) { native = new SmsViewSource(view); }
            public uint ModelCount() => native.ModelCount();
            public bool Model(uint m, out ViewModelInfo info) => native.Model(m, out info);
            public uint Batches(uint m, ViewBatch[] o, uint max) => native.Batches(m, o, max);
            public uint Indices(uint m, int[] o, uint max) => native.Indices(m, o, max);
            public uint Uvs(uint m, float[] o, uint max) => native.Uvs(m, o, max);
            public bool Texture(uint id, out ViewTexture info, byte[] rgba, uint len) => native.Texture(id, out info, rgba, len);
            public uint Draws(ViewDraw[] output, uint max)
            {
                if (output == null) return (uint)count;
                int n = Math.Min(count, (int)max);
                Array.Copy(draws, output, n);
                return (uint)n;
            }
            public IntPtr Positions(out uint count) => native.Positions(out count);
            public IntPtr Normals(out uint count) => native.Normals(out count);
            public IntPtr Colors(out uint count) => native.Colors(out count);
            public IntPtr TextureIds(out uint count) => native.TextureIds(out count);
            public IntPtr FrameUvs(out uint count) => native.FrameUvs(out count);
        }
    }
}
