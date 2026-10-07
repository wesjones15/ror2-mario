using System;
using System.Collections.Generic;
using Ror2Common;
using UnityEngine;
using UnityEngine.Rendering;

namespace MarioTest
{
    // FLUDD's water in the scene: libsms's droplets and splashes (sms_view_water_*), rebuilt
    // every frame as one world-space mesh of camera-facing quads. Drops are see-through in the
    // water's tint over the disc's soft round mask; splashes are the disc's sprite, fading. The
    // game melts overlapping drops into one refracting body; overlapping see-through drops are
    // the cheap stand-in. Unlit (RoR2's Cloud Remap), like RoR2's own water effects.
    internal sealed class MarioWater : IDisposable
    {
        readonly GameObject go;
        readonly Mesh mesh;
        readonly Material drops, splashes;
        readonly Texture2D dropTex, splashTex;
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Color> colors = new List<Color>();
        readonly List<int> dropTris = new List<int>(), splashTris = new List<int>();

        MarioWater(GameObject go, Mesh mesh, Material drops, Material splashes,
            Texture2D dropTex, Texture2D splashTex)
        {
            this.go = go;
            this.mesh = mesh;
            this.drops = drops;
            this.splashes = splashes;
            this.dropTex = dropTex;
            this.splashTex = splashTex;
        }

        /// Null (and says why) without RoR2's Cloud Remap shader.
        public static MarioWater Create(IntPtr view)
        {
            var dropTex = Load(view, 0, "Mario water drop");
            var splashTex = Load(view, 1, "Mario water splash");
            var drops = Shaders.SeeThrough("Mario water", dropTex, false, true);
            var splashes = Shaders.SeeThrough("Mario splash", splashTex, false, true);
            if (!drops || !splashes)
            {
                Plugin.Log.LogWarning("FLUDD's water isn't drawn: no see-through shader");
                return null;
            }
            var go = new GameObject("Mario water");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var mesh = new Mesh { name = "Mario water" };
            mesh.MarkDynamic();
            mesh.subMeshCount = 2;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sharedMaterials = new[] { drops, splashes };
            go.SetActive(false);
            return new MarioWater(go, mesh, drops, splashes, dropTex, splashTex);
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
            uint quads = info.drop_quads + info.splash_quads;
            if (v == null || quads == 0 || count < quads * 4) { Hide(); return; }

            verts.Clear();
            uvs.Clear();
            colors.Clear();
            dropTris.Clear();
            splashTris.Clear();
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
                var tris = q < info.drop_quads ? dropTris : splashTris;
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(dropTris, 0, false);
            mesh.SetTriangles(splashTris, 1, false);
            mesh.RecalculateBounds();

            // The game mixes a little of its tint into what's behind the water (0.35) and shows
            // juice nearly solid (0.75); see-through drops need more body than that to read.
            drops.SetColor("_TintColor", new Color(info.r, info.g, info.b, info.a > 0.5f ? 0.9f : 0.6f));
            if (!go.activeSelf) go.SetActive(true);
        }

        public void Hide()
        {
            if (go && go.activeSelf) go.SetActive(false);
        }

        public void Dispose()
        {
            if (go) UnityEngine.Object.Destroy(go);
            if (mesh) UnityEngine.Object.Destroy(mesh);
            if (drops) UnityEngine.Object.Destroy(drops);
            if (splashes) UnityEngine.Object.Destroy(splashes);
            if (dropTex) UnityEngine.Object.Destroy(dropTex);
            if (splashTex) UnityEngine.Object.Destroy(splashTex);
        }
    }
}
