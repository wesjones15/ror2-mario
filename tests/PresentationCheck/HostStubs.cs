using System;
namespace UnityEngine {
 public class Object { public static implicit operator bool(Object o) => o != null; }
 public class MonoBehaviour : Object { }
 public class Camera : Object { }
 public class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int n) { } }
 public enum EventType { Repaint }
 public class Event { public static Event current = new Event(); public EventType type; }
}
namespace RoR2 {
 public class HealthComponent : UnityEngine.Object { }
 public class PlayerCharacterMasterController : UnityEngine.Object { public NetworkUser networkUser; }
 public class CharacterMaster : UnityEngine.Object { public uint money; public PlayerCharacterMasterController playerCharacterMasterController; }
 public class CharacterBody : UnityEngine.Object {
  public CharacterMaster master; public HealthComponent healthComponent;
  // Stands in for the components a body carries: the check adds the ones it needs (a MarioPuppet for a watched Mario).
  public readonly System.Collections.Generic.Dictionary<Type, object> components = new System.Collections.Generic.Dictionary<Type, object>();
  public T GetComponent<T>() where T : class => components.TryGetValue(typeof(T), out var c) ? (T)c : null;
 }
 public class NetworkUser : UnityEngine.Object { }
 public class CameraRigController : UnityEngine.Object { public UnityEngine.Camera sceneCam; public CharacterBody targetBody; }
 public class LocalUser { public NetworkUser currentNetworkUser; public CharacterMaster cachedMaster; public CharacterBody cachedBody; public CameraRigController cameraRigController; }
 public static class LocalUserManager {
  public static LocalUser first; public static LocalUser GetFirstLocalUser() => first;
  public static System.Collections.Generic.IReadOnlyList<LocalUser> readOnlyLocalUsersList => first == null ? new LocalUser[0] : new[] { first };
 }
 public class Run : UnityEngine.Object {
  public static Run instance;
  public static event Action<Run> onRunDestroyGlobal, onRunStartGlobal;
  public static void End() { onRunDestroyGlobal?.Invoke(instance); instance = null; }
  public static void Begin() { instance = new Run(); onRunStartGlobal?.Invoke(instance); }
 }
}
namespace Ror2Common {
 public static class RunAudio { public static bool Ended; }
 public static class VrHandPanel { public static void Show() { } }
 public static class NativeLib { public static byte[] CString(string s) => Array.Empty<byte>(); }
}
namespace MarioTest {
 static class Plugin { public static bool AssetsReady = true; public static string AssetsDir = "assets"; }
 class MarioDriver { public static MarioDriver Player; }
 // A watched Mario's body carries a puppet whose HudHandle is the native view the HUD reads.
 class MarioPuppet : UnityEngine.MonoBehaviour { public IntPtr HudHandle; }
 static class MarioVr { public static class Character { public static bool WristHud = false; public static void ShowWrist() { } public static void OnWrist(Action draw) { draw(); } } }
 static class Native {
  public static int created, freed;
  public static IntPtr sms_view_new(byte[] _, uint sound) { if(sound != 0) throw new Exception("fallback must be silent"); return new IntPtr(++created); }
  public static void sms_view_free(IntPtr v) { freed++; }
 }
 static class MarioHud { public static int resets, hides; public static void Reset() { resets++; } public static void Hide() { hides++; } public static void Draw() { } }
 static class MarioLife { public static RoR2.HealthComponent health; public static bool retainEmpty; public static void Reset() { health = null; } public static void Update(IntPtr v, RoR2.HealthComponent h, bool keepEmpty = false) { health = h; retainEmpty=keepEmpty; } public static void Draw() { } }
 static class MarioCoinHud { public static RoR2.CharacterMaster master; public static RoR2.LocalUser user; public static IntPtr view; public static void Reset() { master = null; user = null; } public static void UpdateOwned(IntPtr v, RoR2.CharacterMaster m, RoR2.LocalUser u, RoR2.NetworkUser lunarOwner = null) { master=m; user=u; view=v; } public static void Draw() { } }
 static class MarioHudLayout { public static bool on; public static void Restore() { on=false; } public static void Update(bool active, RoR2.LocalUser u) { on=active; } }
 static class MarioHeatHaze { public static void Shutdown() { } }
 static class MarioRadial { public static int resets; public static void Reset() { resets++; } public static void Draw() { } public static void DrawAltIcon() { } public static void DrawInHand() { } }
}
