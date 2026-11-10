using RoR2;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace MarioTest
{
    internal sealed class MarioFallbackEffects
    {
        const string DustKey = "RoR2/Base/Common/VFX/GenericFootstepDust.prefab";
        const string SplashKey = "RoR2/Base/Common/VFX/WaterFootstep.prefab";
        static ParticleSystem dust;
        static GameObject splash;
        static bool triedDust, triedSplash;
        float nextSplash;
        float landingSize;
        Native.Vec3? waterAt;
        bool rocketLaunch;

        /// A Rocket discharged since the last call (the sim's TriggerFired, nozzle 2); for the stand-in smoke.
        public bool TakeRocketLaunch() { var r = rocketLaunch; rocketLaunch = false; return r; }

        public void Begin()
        {
            landingSize = 0f;
            waterAt = null;
        }

        public void Observe(in Native.Event cue)
        {
            switch ((Native.EventKind)cue.kind)
            {
                case Native.EventKind.TriggerFired:
                    if (cue.arg == 2) rocketLaunch = true;
                    break;
                case Native.EventKind.GroundPound:
                    landingSize = 1.9f;
                    break;
                case Native.EventKind.Land:
                    landingSize = Mathf.Max(landingSize, cue.arg != 0 ? 1.4f : 0.85f);
                    break;
                case Native.EventKind.WaterImpact:
                case Native.EventKind.WaterSplash:
                    waterAt ??= cue.pos;
                    break;
            }
        }

        public void Draw(MarioView view, Native.Vec3 feet)
        {
            if (!Plugin.Effects.Value || view?.HasEffects == true) return;
            if (landingSize > 0f) Land(UnityHost.ToUnity(feet), landingSize);
            if (waterAt.HasValue) WaterImpact(view, UnityHost.ToUnity(waterAt.Value));
        }

        static GameObject Load(string key)
        {
            try
            {
                var prefab = Addressables.LoadAssetAsync<GameObject>(key).WaitForCompletion();
                if (prefab) Plugin.Log.LogDebug($"Loaded RoR2 fallback effect {prefab.name}.");
                else Plugin.Log.LogWarning($"RoR2 fallback effect {key} was unavailable.");
                return prefab;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Mario's fallback effect {key} is unavailable: {e.Message}");
                return null;
            }
        }

        // TMario::smallTouchDownEffect and strongTouchDownEffect emit MS_JUMP_ED_A/B.
        // TMario::hipAttacking emits MS_HIPDROP_A..C on a normal pound.
        static void Land(Vector3 feet, float size)
        {
            if (!triedDust)
            {
                triedDust = true;
                var prefab = Load(DustKey);
                if (prefab) dust = prefab.GetComponent<ParticleSystem>();
            }
            if (!dust) return;
            var puff = Object.Instantiate(dust, feet + Vector3.up * (0.14f * size), Quaternion.identity);
            var main = puff.main;
            main.startSize = size;
            main.startLifetime = size >= 1.4f ? 0.45f : 0.3f;
            var emission = puff.emission;
            emission.enabled = false;
            puff.Play();
            puff.Emit(1);
            Object.Destroy(puff.gameObject, 1f);
        }

        // TModelWaterManager::move calls TSplashManager::newSplash on impacts.
        // Limit the stock effect per Mario so a spray can hit many times without flooding the view.
        public void WaterImpact(MarioView view, Vector3 at)
        {
            if (!Plugin.Effects.Value || view?.HasEffects == true || Time.time < nextSplash) return;
            if (!triedSplash)
            {
                triedSplash = true;
                splash = Load(SplashKey);
            }
            if (!splash) return;
            nextSplash = Time.time + 0.125f;
            EffectManager.SpawnEffect(splash, new EffectData
            {
                origin = at + Vector3.up * 0.1f,
                rotation = Quaternion.identity,
                scale = 0.55f
            }, false);
        }
    }
}
