// Only the host APIs referenced by the real MarioSkin implementation.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object { public string name; public static implicit operator bool(Object o) => o != null; }
    public class ScriptableObject : Object { public static T CreateInstance<T>() where T : new() => new T(); }
    public class GameObject : Object { public T GetComponent<T>() where T : new() => new T(); public T AddComponent<T>() where T : new() => new T(); }
    public class Transform : Object { public GameObject gameObject = new GameObject(); public T GetComponent<T>() where T : new() => new T(); }
    public struct Color { public Color(float r,float g,float b) {} }
    public struct Color32 { public static explicit operator Color32(Color c) => default; }
    public struct Rect { public Rect(float x,float y,float w,float h) {} }
    public struct Vector2 { public Vector2(float x,float y) {} }
    public enum TextureFormat { RGBA32 }
    public class Texture2D : Object { public int width=8,height=8; public Texture2D(int w,int h,TextureFormat f,bool m) {} public void SetPixelData(Color32[] c,int n) {} public void Apply(bool a,bool b) {} }
    public class Sprite : Object { public static Sprite Create(Texture2D t,Rect r,Vector2 v) => new Sprite(); }
    public static class Mathf { public static uint Max(uint a,uint b) => Math.Max(a,b); }
}
namespace RoR2
{
    using UnityEngine;
    public enum BodyIndex { None=-1, Mario=0 }
    public static class BodyCatalog { public static BodyIndex FindBodyIndex(string n) => BodyIndex.Mario; }
    public class BodyLoadoutManager { public uint GetSkinIndex(BodyIndex i) => 0; }
    public class Loadout { public BodyLoadoutManager bodyLoadoutManager = new BodyLoadoutManager(); }
    public class CharacterMaster : Object { public uint money; public Loadout loadout = new Loadout(); }
    public class CharacterBody : Object { public uint skinIndex; public BodyIndex bodyIndex; public CharacterMaster master; public HealthComponent healthComponent; }
    public class HealthComponent : Object { public bool alive; }
    public class Run : Object { public static Run instance; public int stageClearCount; }
    public class TeleporterInteraction : Object { public static TeleporterInteraction instance; public int activationState; }
    public class UserProfile { readonly HashSet<UnlockableDef> owned = new HashSet<UnlockableDef>(); public int Saves; public Loadout loadout = new Loadout(); public bool HasUnlockable(UnlockableDef u) => owned.Contains(u); public void GrantUnlockable(UnlockableDef u) => owned.Add(u); public void RequestEventualSave() => Saves++; }
    public class LocalUser { public UserProfile userProfile; public CharacterBody cachedBody; }
    public static class LocalUserManager { public static List<LocalUser> readOnlyLocalUsersList = new List<LocalUser>(); }
    public class UnlockableDef : Object { public string cachedName,nameToken; public Func<string> getHowToUnlockString,getUnlockedString; }
    public class SkinDef : Object { public string nameToken; public GameObject rootObject; public SkinDef[] baseSkins; public Sprite icon; public UnlockableDef unlockableDef; public SkinDefParams skinDefParams,optimizedSkinDefParams; }
    public class SkinDefParams : Object { public struct GameObjectActivation {} public struct MeshReplacement {} public struct ProjectileGhostReplacement {} public struct MinionSkinReplacement {} public object rendererInfos; public GameObjectActivation[] gameObjectActivations; public MeshReplacement[] meshReplacements; public ProjectileGhostReplacement[] projectileGhostReplacements; public MinionSkinReplacement[] minionSkinReplacements; public CharacterModel.LightInfo[] lightReplacements; }
    public class ModelLocator { public Transform modelTransform = new Transform(); }
    public class ModelSkinController : Object { public GameObject gameObject = new GameObject(); public SkinDef[] skins; }
    public class CharacterModel { public struct LightInfo {} public object baseRendererInfos; }
    public class Language { public void SetStringByToken(string a,string b) {} }
}
namespace RoR2.ContentManagement { public class Collection<T> { public void Add(T[] a) {} } public class ContentPack { public Collection<RoR2.UnlockableDef> unlockableDefs = new Collection<RoR2.UnlockableDef>(); } }
namespace Ror2Common
{
    public static class NativeLib { public static byte[] CString(string s) => null; }
    public enum PortraitFraming { ThreeQuarter }
    public static class Portrait { public static void Request(UnityEngine.GameObject b,Func<object> f,Action<UnityEngine.Texture2D> a,PortraitFraming framing) {} }
}
namespace MarioTest
{
    public class ConfigValue<T> { public T Value; }
    public class Logger { public void LogInfo(string s) {} }
    public static class Plugin { public static ConfigValue<float> LuigiJumpHeight = new ConfigValue<float>(); public static ConfigValue<bool> PiantissimoGameVoice = new ConfigValue<bool>(); public static Logger Log = new Logger(); }
    public static class MarioDisplay { public static object Get(uint id) => null; }
    public static class Native { public static uint sms_view_load_skin(IntPtr v,uint s,byte[] d) => 1; public static void sms_view_skin(IntPtr v,uint s) {} public static void sms_view_set_voice_pitch(IntPtr v,float p) {} public static void sms_view_voice_set(IntPtr v,uint s) {} }
}
