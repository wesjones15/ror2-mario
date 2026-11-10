using RoR2;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace MarioTest
{
    // The stand-in's flames (the super ground pound, a burning corkscrew or blazing slide, a flaming Rocket
    // spin; the sim's POSE_FIRE cue): the plain Lemurian's fireball, the visual of the projectile it spits
    // (RoR2's FireballGhost), taken off the projectile and burning around Mario's middle. Its particles live in
    // world space, so they stream behind him as he moves. If the prefab can't be loaded the generic burning
    // effect (MarioFlames) is used instead.
    internal sealed class LemurianFlames
    {
        const string Key = "RoR2/Base/Characters/Lemurian/Skills/FireballGhost.prefab";
        static GameObject prefab;
        static bool tried;
        readonly MarioFlames fallback = new MarioFlames();
        GameObject fire;
        ParticleSystem[] systems;

        /// Burns while `on`, centred on `centre`, sized for a Mario `height` metres tall.
        public void Set(bool on, Vector3 centre, float height)
        {
            if (!tried) { tried = true; prefab = Load(); }
            if (!prefab) { fallback.Set(on, centre, height); return; }
            if (on && !fire) Light(height);
            if (!fire) return;
            if (on)
            {
                fire.transform.position = centre;
                return;
            }
            foreach (var ps in systems)
                if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            Object.Destroy(fire, 2f);
            fire = null;
        }

        static GameObject Load()
        {
            try
            {
                var loaded = Addressables.LoadAssetAsync<GameObject>(Key).WaitForCompletion();
                if (loaded) Plugin.Log.LogInfo($"Stand-in flames: the Lemurian fireball ({loaded.name}) loaded.");
                else Plugin.Log.LogWarning($"Stand-in flames: {Key} was unavailable; using RoR2's burning effect.");
                return loaded;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Stand-in flames: {Key} failed to load ({e.Message}); using RoR2's burning effect.");
                return null;
            }
        }

        void Light(float height)
        {
            fire = Object.Instantiate(prefab);
            // It is a projectile's ghost: without its projectile it would follow nothing and destroy itself.
            foreach (var b in fire.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!b) continue;
                string name = b.GetType().Name;
                if (name == "ProjectileGhostController" || name == "DestroyOnTimer") Object.DestroyImmediate(b);
            }
            // Sized to burn around a Mario this tall (the fireball is about a body's width).
            fire.transform.localScale = Vector3.one * Mathf.Max(0.5f, height * 0.75f);
            systems = fire.GetComponentsInChildren<ParticleSystem>(true);
            fire.SetActive(true);
            foreach (var ps in systems) ps.Play(true);
            Plugin.Log.LogDebug($"Stand-in flames lit: {systems.Length} particle systems.");
        }
    }
}
