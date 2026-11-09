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

        static GameObject Load(string key)
        {
            try
            {
                return Addressables.LoadAssetAsync<GameObject>(key).WaitForCompletion();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Mario's fallback effect {key} is unavailable: {e.Message}");
                return null;
            }
        }

        // TMario::hipAttacking in MarioJump.cpp emits the normal landing's MS_HIPDROP_A..C.
        // The stock one-shot supplies the dust cue when the local view has no Sunshine FX.
        public static void Land(MarioView view, Vector3 feet)
        {
            if (!Plugin.Effects.Value || view?.HasEffects == true) return;
            if (!triedDust)
            {
                triedDust = true;
                var prefab = Load(DustKey);
                if (prefab) dust = prefab.GetComponent<ParticleSystem>();
            }
            if (!dust) return;
            var puff = Object.Instantiate(dust, feet + Vector3.up * 0.12f, Quaternion.identity);
            puff.transform.localScale *= 1.25f;
            puff.Play();
            Object.Destroy(puff.gameObject, 1f);
        }

        // TModelWaterManager::move calls TSplashManager::newSplash on enemy hits.
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
                origin = at,
                rotation = Quaternion.identity,
                scale = 0.28f
            }, false);
        }
    }
}
