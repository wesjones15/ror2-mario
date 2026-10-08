using RoR2;
using UnityEngine;

namespace MarioTest
{
    // The super ground pound's flames when the disc's particle effects aren't in the assets
    // folder (MarioFx draws the game's own, MS_M_SPHIPD_A..D, otherwise): RoR2's burning effect
    // (the fire BurnEffectController puts on a burning body) stands in, emitted from a ball
    // around Mario's middle. The particles live in world space, so they stream up behind him as he
    // falls, and fade out on the landing.
    internal sealed class MarioFlames
    {
        GameObject fire;
        ParticleSystem[] systems;

        /// Burns while `on`, centred on `centre`, sized for a Mario `height` metres tall.
        public void Set(bool on, Vector3 centre, float height)
        {
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

        void Light(float height)
        {
            var prefab = BurnEffectController.normalEffect?.fireEffectPrefab
                ?? LegacyResourcesAPI.Load<GameObject>("Prefabs/FireEffect");
            if (!prefab) return;
            fire = Object.Instantiate(prefab);
            systems = fire.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var ps in systems)
            {
                // BurnEffectController shapes it to the burning mesh; a ball does for Mario.
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = height * 0.35f;
            }
            fire.SetActive(true);
            foreach (var ps in systems) ps.Play(true);
        }
    }
}
