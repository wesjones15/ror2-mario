using RoR2;
using UnityEngine;

namespace MarioTest
{
    // The spinning ground pound's landing: the pound's fire (RoR2's burning effect, which
    // MarioFlames also uses for the falling pound) laid out as a flat ring on the ground that
    // sweeps out from the impact to the shock wave's radius quickly, then lets go and fades.
    internal sealed class MarioFireRing : MonoBehaviour
    {
        /// Seconds the ring takes to reach full radius, and the whole effect's life.
        const float Grow = 0.3f, Life = 0.55f;

        ParticleSystem[] systems;
        float radius, born;
        bool stopped;

        internal static void Spawn(Vector3 at, float radius)
        {
            var prefab = BurnEffectController.normalEffect?.fireEffectPrefab
                ?? LegacyResourcesAPI.Load<GameObject>("Prefabs/FireEffect");
            if (!prefab || radius <= 0f) return;
            var go = Instantiate(prefab, at + Vector3.up * 0.1f, Quaternion.Euler(-90f, 0f, 0f));
            var ring = go.AddComponent<MarioFireRing>();
            ring.radius = radius;
            ring.born = Time.time;
            ring.systems = go.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var ps in ring.systems)
            {
                var main = ps.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                // Short-lived and quick: it should stay a thin ring, not a pillar.
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
                var shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = 0.2f;
                shape.radiusThickness = 0.12f; // from the rim only
                shape.rotation = Vector3.zero;
                var emission = ps.emission;
                emission.rateOverTime = Mathf.Max(emission.rateOverTime.constant, 30f) * Mathf.Clamp(radius, 2f, 12f);
            }
            go.SetActive(true);
            foreach (var ps in ring.systems) ps.Play(true);
            Destroy(go, Life + 1.5f);
        }

        void Update()
        {
            float t = Time.time - born;
            // Ease out: it leaps outward at first, then settles on the edge of the blast.
            float u = Mathf.Clamp01(t / Grow);
            float r = Mathf.Max(0.2f, radius * (1f - (1f - u) * (1f - u)));
            foreach (var ps in systems)
            {
                if (!ps) continue;
                var shape = ps.shape;
                shape.radius = r;
            }
            if (!stopped && t >= Life)
            {
                stopped = true;
                foreach (var ps in systems)
                    if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }
}
