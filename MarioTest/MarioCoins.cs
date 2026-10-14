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
        const int Capacity = 128;
        const float Lifetime = 60f, PickupDelay = 0.3f;
        sealed class Coin
        {
            public Vector3 pos, velocity;
            public TeamIndex team;
            public float age;
            public bool settled;
        }

        readonly Coin[] coins = new Coin[Capacity];
        readonly System.Collections.Generic.HashSet<int> rewarded = new System.Collections.Generic.HashSet<int>();
        CoinSource source;
        ModelView models;
        IntPtr view;
        int next;
        bool tried;

        void Awake()
        {
            GlobalEventManager.onCharacterDeathGlobal += Killed;
            SceneManager.activeSceneChanged += SceneChanged;
            Run.onRunDestroyGlobal += RunEnded;
        }

        void OnDestroy()
        {
            GlobalEventManager.onCharacterDeathGlobal -= Killed;
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
            coins[next] = new Coin
            {
                pos = victim.corePosition,
                velocity = UnityEngine.Random.insideUnitSphere * (80f * unit) + Vector3.up * (220f * unit),
                team = attacker.teamComponent.teamIndex,
            };
            next = (next + 1) % Capacity;
        }

        void FixedUpdate()
        {
            if (!NetworkServer.active || !Run.instance || PauseManager.isPaused) return;
            float dt = Time.fixedDeltaTime;
            float unit = UnityHost.Scale;
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
                if (coin.age < PickupDelay) continue;
                foreach (var body in CharacterBody.readOnlyInstancesList)
                {
                    if (!body || !body.healthComponent || !body.healthComponent.alive || !body.teamComponent
                        || body.teamComponent.teamIndex != coin.team) continue;
                    var mario = body.GetComponent<MarioDriver>();
                    if (!mario) continue;
                    var feet = body.footPosition;
                    var delta = coin.pos - feet;
                    // coin_hit_data_table: radius 70, height 100; Mario's cylinder adds 50/160.
                    if (delta.x * delta.x + delta.z * delta.z > 120f * 120f * unit * unit
                        || coin.pos.y + 100f * unit < feet.y || coin.pos.y > feet.y + 160f * unit) continue;
                    // Remove first: healing can trigger item callbacks, but a coin heals once.
                    coins[i] = null;
                    body.healthComponent.Heal(body.healthComponent.fullCombinedHealth / 8f, default, true);
                    mario.CoinCollected(coin.pos);
                    break;
                }
            }
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
        sealed class CoinSource : IViewSource
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
