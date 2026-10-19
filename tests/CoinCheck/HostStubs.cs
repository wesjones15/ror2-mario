// Only the host APIs referenced by MarioCoins. Physics/rendering are not benchmarked here.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        static int next;
        readonly int id = ++next;
        public int GetInstanceID() => id;
        public static implicit operator bool(Object value) => value != null;
    }
    public class MonoBehaviour : Object { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public static Vector3 up => new Vector3(0, 1, 0);
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
        public static Vector3 operator /(Vector3 a, float b) => a * (1f / b);
        public static Vector3 ProjectOnPlane(Vector3 a, Vector3 b) => a;
    }
    public struct Quaternion { public float x, y, z, w; public static Quaternion Euler(float x, float y, float z) => default; }
    public static class Random { public static Vector3 insideUnitSphere => default; }
    public static class Time { public static float fixedDeltaTime = 1f / 60f; public static float time; }
    public enum QueryTriggerInteraction { Ignore }
    public struct RaycastHit { public Vector3 point, normal; }
    public static class Physics
    {
        public static Vector3 gravity => new Vector3(0, -9.81f, 0);
        public static bool SphereCast(Vector3 p, float r, Vector3 d, out RaycastHit hit, float distance, int mask, QueryTriggerInteraction q)
        { hit = default; return false; }
    }
}
namespace UnityEngine.Networking { public static class NetworkServer { public static bool active; } }
namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public static class SceneManager { public static event Action<Scene, Scene> activeSceneChanged { add { } remove { } } }
}
namespace RoR2
{
    using UnityEngine;
    public enum TeamIndex { Player, Monster }
    public class BodyList : IEnumerable<CharacterBody>
    {
        readonly List<CharacterBody> bodies = new List<CharacterBody>();
        public int visits;
        public void Add(CharacterBody body) => bodies.Add(body);
        public void Clear() => bodies.Clear();
        public IEnumerator<CharacterBody> GetEnumerator() { foreach (var body in bodies) { visits++; yield return body; } }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public class NetId { public uint Value; }
    public class TeamComponent : Object { public TeamIndex teamIndex; }
    public class CharacterBody : Object
    {
        public static readonly BodyList readOnlyInstancesList = new BodyList();
        public static int lookups;
        public HealthComponent healthComponent;
        public TeamComponent teamComponent;
        public CharacterMaster master;
        public Vector3 corePosition, footPosition;
        public bool hasEffectiveAuthority;
        public NetId netId = new NetId { Value = 1 };
        internal MarioTest.MarioDriver mario;
        public T GetComponent<T>() where T : class { lookups++; return mario as T; }
    }
    public class CharacterMaster : Object { public uint money; public void GiveMoney(uint value, bool scale) { money += value; } }
    public class HealthComponent : Object
    {
        public bool alive = true;
        public float fullCombinedHealth = 80f, healed;
        public int heals;
        public Action onHeal;
        public void Heal(float amount, object mask, bool nonRegen) { heals++; healed += amount; onHeal?.Invoke(); }
    }
    public class Run : Object { public static Run instance; public static event Action<Run> onRunDestroyGlobal { add { } remove { } } }
    public static class PauseManager { public static bool isPaused; }
    public class DamageReport { public CharacterBody attackerBody, victimBody; public HealthComponent victim; }
    public static class GlobalEventManager { public static event Action<DamageReport> onDeathProcServer { add { } remove { } } }
    public struct TeamMask { public static TeamMask GetEnemyTeams(TeamIndex t) => default; public bool HasTeam(TeamIndex t) => true; }
    public struct LayerIndex { public int mask; public static LayerIndex world => default; }
}
namespace Ror2Common
{
    public interface IViewSource { }
    public interface IStaticGeometrySource { }
    public struct ViewVec3 { public float x, y, z; }
    public struct ViewDraw { public ulong key; public int model; public ViewVec3 pos; public float rot_x, rot_y, rot_z, rot_w, alpha; }
    public struct ViewModelInfo { }
    public struct ViewBatch { }
    public struct ViewTexture { }
    public static class NativeLib { public static byte[] CString(string s) => Array.Empty<byte>(); }
    public class ModelView : IDisposable
    {
        public static ModelView Create(IViewSource s, string name) => new ModelView();
        public void Dispose() { }
        public void Update(float scale) { }
    }
}
namespace MarioTest
{
    using UnityEngine;
    using Ror2Common;
    internal class MarioDriver : Object { }
    internal static class MarioPerformance
    {
        internal enum Part { Coins }
        internal struct Timing : IDisposable { public void Dispose() { } }
        internal static Timing Measure(Part p) => default;
    }
    internal class Setting<T> { public T Value; public Setting(T value) { Value = value; } }
    internal class Logger { public void LogError(string text) { } }
    internal static class Plugin
    {
        public static bool AssetsReady = false;
        public static string AssetsDir = "";
        public static Logger Log = new Logger();
        public static Setting<int> CoinGold = new Setting<int>(3);
    }
    internal static class Native
    {
        internal struct Vec3 { public float x, y, z; }
        public static void sms_view_free(IntPtr view) { }
        public static IntPtr sms_view_new_look(byte[] path, int sound, int look) => IntPtr.Zero;
    }
    internal static class UnityHost
    {
        public static float Scale => .01f;
        public static Native.Vec3 ToSim(Vector3 v) => new Native.Vec3 { x = v.x / Scale, y = v.y / Scale, z = v.z / Scale };
    }
    internal static class MarioNet
    {
        public static int taken, collected;
        public static void SendCoin(int i, Vector3 p, Vector3 v, RoR2.TeamIndex t) { }
        public static void CoinCollected(RoR2.CharacterBody b, Vector3 p) { collected++; }
        public static void SendCoinTaken(int i, RoR2.CharacterBody b, Vector3 p) { taken++; }
        public static int touches;
        public static void SendCoinTouch(int i, RoR2.CharacterBody b) { touches++; }
    }
    internal class SmsViewSource
    {
        public SmsViewSource(IntPtr view) { }
        public uint ModelCount() => 0;
        public bool Model(uint m, out ViewModelInfo info) { info = default; return false; }
        public uint Batches(uint m, ViewBatch[] o, uint max) => 0;
        public uint Indices(uint m, int[] o, uint max) => 0;
        public uint Uvs(uint m, float[] o, uint max) => 0;
        public bool Texture(uint id, out ViewTexture info, byte[] rgba, uint len) { info = default; return false; }
        public IntPtr Positions(out uint count) { count = 0; return IntPtr.Zero; }
        public IntPtr Normals(out uint count) { count = 0; return IntPtr.Zero; }
        public IntPtr Colors(out uint count) { count = 0; return IntPtr.Zero; }
        public IntPtr TextureIds(out uint count) { count = 0; return IntPtr.Zero; }
        public IntPtr FrameUvs(out uint count) { count = 0; return IntPtr.Zero; }
    }
}
