using System;
using System.Collections.Generic;
using Ror2Common;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;

namespace MarioTest
{
    // FLUDD's water in the scene: libsms's droplets and splashes (sms_view_water_*), rebuilt
    // every frame as one world-space mesh of camera-facing quads. The game melts overlapping
    // drops into one body that bends what's behind it and mixes in a little of its tint
    // (`drawRefracAndSpec`, 0.35). Here each drop is drawn twice: through RoR2's own
    // refraction material (matDistortion, a sphere's normals bending the screen behind it), and
    // faintly in the water's tint over the disc's soft round mask (Look.WaterOpacity, low, as
    // overlapping drops add up). Splashes are the disc's sprite, fading. Unlit (RoR2's Cloud
    // Remap), like RoR2's own water effects.
    internal sealed class MarioWater : IDisposable
    {
        readonly GameObject go;
        readonly Mesh mesh;
        readonly Material drops, splashes, wetness, refraction;
        readonly Texture2D dropTex, splashTex;
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Color> colors = new List<Color>();
        readonly List<int> dropTris = new List<int>(), splashTris = new List<int>();
        readonly List<int> wetTris = new List<int>();
        static readonly List<int> none = new List<int>();

        MarioWater(GameObject go, Mesh mesh, Material drops, Material splashes, Material wetness, Material refraction,
            Texture2D dropTex, Texture2D splashTex)
        {
            this.go = go;
            this.mesh = mesh;
            this.drops = drops;
            this.splashes = splashes;
            this.wetness = wetness;
            this.refraction = refraction;
            this.dropTex = dropTex;
            this.splashTex = splashTex;
        }

        /// Null (and says why) without RoR2's Cloud Remap shader.
        public static MarioWater Create(IntPtr view)
        {
            var dropTex = Load(view, 0, "Mario water drop");
            var splashTex = Load(view, 1, "Mario water splash");
            var drops = Shaders.SeeThrough("Mario water", dropTex, false, true, Shaders.SurfaceTintOpacity);
            var splashes = Shaders.SeeThrough("Mario splash", splashTex, false, true, Shaders.SurfaceTintOpacity);
            var wetness = Shaders.SeeThrough("Mario wet terrain", dropTex, false, true);
            if (!drops || !splashes || !wetness)
            {
                Plugin.Log.LogWarning("FLUDD's water isn't drawn: no see-through shader");
                return null;
            }
            // The tint over the refraction, then the splashes.
            drops.renderQueue = 3001;
            splashes.renderQueue = 3002;
            // A soft black overlay darkens the receiving terrain's existing colour. These
            // marks share the native sim's wet-slide footprint and drying lifetime.
            wetness.renderQueue = 2998;
            wetness.SetColor("_TintColor", new Color(0f, 0f, 0f, Mathf.Clamp01(Plugin.GroundWetness.Value)));
            var refraction = Plugin.WaterRefraction.Value > 0f ? Refraction("Mario water refraction") : null;
            var go = new GameObject("Mario water");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var mesh = new Mesh { name = "Mario water" };
            mesh.MarkDynamic();
            mesh.subMeshCount = refraction ? 4 : 3;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sharedMaterials = refraction ? new[] { drops, splashes, wetness, refraction } : new[] { drops, splashes, wetness };
            go.SetActive(false);
            return new MarioWater(go, mesh, drops, splashes, wetness, refraction, dropTex, splashTex);
        }

        // Addressables' public key differs from the asset's internal Distortion/ directory.
        const string DistortionKey = "RoR2/Base/Common/VFX/matDistortion.mat";
        static readonly int MagnitudeId = Shader.PropertyToID("_Magnitude");

        // A copy of RoR2's refraction material, its strength scaled by Look.WaterRefraction;
        // null (FLUDD's water is tint alone) if it won't load.
        internal static Material Refraction(string name, Texture bump = null, Texture mask = null, float normalGain = 1f)
        {
            try
            {
                var src = Addressables.LoadAssetAsync<Material>(DistortionKey).WaitForCompletion();
                if (!src || !src.shader)
                {
                    Plugin.Log.LogWarning("FLUDD's water: RoR2's refraction material didn't load; tint only");
                    return null;
                }
                var m = new Material(src) { name = name };
                if (m.HasProperty(MagnitudeId))
                    m.SetFloat(MagnitudeId, m.GetFloat(MagnitudeId) * Plugin.WaterRefraction.Value / normalGain);
                if (bump)
                {
                    // Slide ribbons use the disc's indirect wave map instead of the water
                    // drop's sphere normal. Both use this same existing refraction pass.
                    m.SetTexture("_BumpMap", bump);
                    m.SetTexture("_MaskTex", mask ? mask : Texture2D.whiteTexture);
                    // Ribbons meet the ground along their lower edge. The sphere effect's
                    // broad soft-particle fade would erase their shallow side crests.
                    if (m.HasProperty("_InvFade")) m.SetFloat("_InvFade", 2f);
                    // Ribbons are crossed single-sided sheets seen from either side.
                    if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                }
                Plugin.Log.LogInfo($"{name} ({m.shader.name}, queue {m.renderQueue}" +
                    (m.HasProperty(MagnitudeId) ? $", magnitude {m.GetFloat(MagnitudeId):0.##})" : ")"));
                return m;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"FLUDD's water: no refraction ({e.Message}); tint only");
                return null;
            }
        }

        static Texture2D Load(IntPtr view, uint which, string name)
        {
            if (Native.sms_view_water_texture(view, which, out var t, null, 0) == 0) return null;
            var px = new byte[t.width * t.height * 4];
            if (Native.sms_view_water_texture(view, which, out t, px, (uint)px.Length) == 0) return null;
            return ModelView.MakeTexture(name, t, px);
        }

        /// This frame's water (after sms_view_update), `scale` metres per sim unit.
        public unsafe void Update(IntPtr view, float scale)
        {
            if (Native.sms_view_water_info(view, out var info) == 0) { Hide(); return; }
            var v = (Native.WaterVertex*)Native.sms_view_water_vertices(view, out uint count);
            uint waterQuads = info.drop_quads + info.splash_quads;
            uint quads = waterQuads + info.wet_quads;
            if (v == null || quads == 0 || count < quads * 4) { Hide(); return; }

            verts.Clear();
            uvs.Clear();
            colors.Clear();
            dropTris.Clear();
            splashTris.Clear();
            wetTris.Clear();
            for (uint q = 0; q < quads; q++)
            {
                int b = verts.Count;
                for (uint c = 0; c < 4; c++)
                {
                    var w = v[q * 4 + c];
                    verts.Add(new Vector3(w.pos.x, w.pos.y, w.pos.z) * scale);
                    uvs.Add(new Vector2(w.u, w.v));
                    colors.Add(new Color(1f, 1f, 1f, w.alpha));
                }
                var tris = q < info.drop_quads ? dropTris : q < waterQuads ? splashTris : wetTris;
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.subMeshCount = refraction ? 4 : 3;
            mesh.SetTriangles(dropTris, 0, false);
            mesh.SetTriangles(splashTris, 1, false);
            mesh.SetTriangles(wetTris, 2, false);
            // Water bends what's behind it; Yoshi's juice is near solid and doesn't.
            if (refraction) mesh.SetTriangles(info.a > 0.5f ? none : dropTris, 3, false);
            mesh.RecalculateBounds();

            // The game mixes a little of its tint into what's behind the water (0.35) and shows
            // juice nearly solid (0.75). Overlapping drops add up, so each takes a little.
            float a = info.a > 0.5f ? 0.9f : Mathf.Clamp01(Plugin.WaterOpacity.Value);
            drops.SetFloat("_AlphaBoost", info.a > 0.5f ? 1f : Shaders.SurfaceTintOpacity);
            drops.SetColor("_TintColor", new Color(info.r, info.g, info.b, a));
            wetness.SetColor("_TintColor", new Color(0f, 0f, 0f, Mathf.Clamp01(Plugin.GroundWetness.Value)));
            if (!go.activeSelf) go.SetActive(true);
        }

        public void Hide()
        {
            if (go && go.activeSelf) go.SetActive(false);
        }

        /// Offscreen portrait layer for the water mesh.
        public void SetLayer(int layer)
        {
            if (go) go.layer = layer;
        }

        public void Dispose()
        {
            if (go) UnityEngine.Object.Destroy(go);
            if (mesh) UnityEngine.Object.Destroy(mesh);
            if (drops) UnityEngine.Object.Destroy(drops);
            if (splashes) UnityEngine.Object.Destroy(splashes);
            if (wetness) UnityEngine.Object.Destroy(wetness);
            if (refraction) UnityEngine.Object.Destroy(refraction);
            if (dropTex) UnityEngine.Object.Destroy(dropTex);
            if (splashTex) UnityEngine.Object.Destroy(splashTex);
        }
    }
}
