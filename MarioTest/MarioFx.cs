using System;
using System.Collections.Generic;
using Ror2Common;
using UnityEngine;
using UnityEngine.Rendering;

namespace MarioTest
{
    // The game's own particle effects on Mario, from libsms (sms_view_fx_*: the disc's .jpa
    // effects run as JParticle runs them): the super ground pound's flames. Rebuilt every
    // frame as one world-space mesh, a submesh per batch, each batch a texture times its vertex
    // colours, added (or see-through) on RoR2's Cloud Remap, unlit. libsms bakes the game's
    // colour mixing into the textures and colours, so plain texture-times-colour adds up to it.
    internal sealed class MarioFx : IDisposable
    {
        readonly GameObject go;
        readonly Mesh mesh;
        readonly MeshRenderer renderer;
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Color> colors = new List<Color>();
        readonly List<int> tris = new List<int>();
        readonly List<Material> shown = new List<Material>();
        readonly Dictionary<long, Material> materials = new Dictionary<long, Material>();
        Texture2D[] textures = new Texture2D[0];
        uint[] versions = new uint[0];
        byte[] scratch = new byte[0];

        MarioFx(GameObject go, Mesh mesh, MeshRenderer renderer)
        {
            this.go = go;
            this.mesh = mesh;
            this.renderer = renderer;
        }

        /// Null (and says why) without effects to draw or RoR2's Cloud Remap shader.
        public static MarioFx Create(IntPtr view)
        {
            if (Native.sms_view_fx_info(view, out var info) == 0 || info.textures == 0)
            {
                Plugin.Log.LogWarning("Sunshine's particle effects aren't in the assets folder; " +
                    "the super pound burns with RoR2's fire instead");
                return null;
            }
            if (!Shaders.CloudRemap())
            {
                Plugin.Log.LogWarning("Sunshine's particle effects aren't drawn: no see-through shader");
                return null;
            }
            var go = new GameObject("Mario effects");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var mesh = new Mesh { name = "Mario effects" };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            Plugin.Log.LogInfo($"Sunshine's particle effects: {info.textures} textures");
            return new MarioFx(go, mesh, r);
        }

        /// The texture `which`, uploaded (again when libsms says its pixels changed).
        Texture2D Texture(IntPtr view, uint which)
        {
            if (which >= textures.Length)
            {
                Array.Resize(ref textures, (int)which + 1);
                Array.Resize(ref versions, (int)which + 1);
            }
            uint version = Native.sms_view_fx_texture_version(view, which);
            var tex = textures[which];
            if (tex && versions[which] == version) return tex;
            if (Native.sms_view_fx_texture(view, which, out var t, null, 0) == 0) return tex;
            int len = (int)(t.width * t.height * 4);
            if (scratch.Length < len) scratch = new byte[len];
            if (Native.sms_view_fx_texture(view, which, out t, scratch, (uint)scratch.Length) == 0) return tex;
            if (!tex || tex.width != t.width || tex.height != t.height)
            {
                if (tex) UnityEngine.Object.Destroy(tex);
                // Kept readable: some (a stripe's whole look) change every game frame.
                tex = new Texture2D((int)t.width, (int)t.height, TextureFormat.RGBA32, false)
                {
                    name = $"Mario effect {which}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = t.wrap_s == 1 ? TextureWrapMode.Clamp : TextureWrapMode.Repeat,
                };
                textures[which] = tex;
                // A new texture: materials holding the old one are rebuilt.
                foreach (var key in new List<long>(materials.Keys))
                    if ((uint)(key >> 8) == which)
                    {
                        UnityEngine.Object.Destroy(materials[key]);
                        materials.Remove(key);
                    }
            }
            var px = new byte[len];
            Buffer.BlockCopy(scratch, 0, px, 0, len);
            tex.SetPixelData(px, 0);
            tex.Apply(false, false);
            versions[which] = version;
            return tex;
        }

        Material MaterialFor(IntPtr view, uint texture, uint blend)
        {
            var tex = Texture(view, texture);
            long key = ((long)texture << 8) | blend;
            if (materials.TryGetValue(key, out var m) && m) return m;
            m = Shaders.SeeThrough($"Mario effect {texture}", tex, blend == 0, true);
            materials[key] = m;
            return m;
        }

        /// This frame's effects (after sms_view_update): sim units times `scale`, shifted by
        /// `offset` (where Mario is drawn against where the sim has him).
        public unsafe void Update(IntPtr view, float scale, Vector3 offset)
        {
            if (Native.sms_view_fx_info(view, out var info) == 0 || info.batches == 0) { Hide(); return; }
            var v = (Native.FxVertex*)Native.sms_view_fx_vertices(view, out uint count);
            var b = (Native.FxBatch*)Native.sms_view_fx_batches(view, out uint batches);
            if (v == null || b == null || batches == 0) { Hide(); return; }

            verts.Clear();
            uvs.Clear();
            colors.Clear();
            shown.Clear();
            mesh.Clear();
            for (uint i = 0; i < count; i++)
            {
                var w = v[i];
                verts.Add(new Vector3(w.pos.x, w.pos.y, w.pos.z) * scale + offset);
                uvs.Add(new Vector2(w.u, w.v));
                colors.Add(new Color(w.r, w.g, w.b, w.a));
            }
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.subMeshCount = (int)batches;
            for (uint i = 0; i < batches; i++)
            {
                var batch = b[i];
                tris.Clear();
                uint end = Math.Min(batch.first_vertex + batch.vertex_count, count);
                for (uint q = batch.first_vertex; q + 4 <= end; q += 4)
                {
                    int c = (int)q;
                    tris.Add(c); tris.Add(c + 1); tris.Add(c + 2);
                    tris.Add(c); tris.Add(c + 2); tris.Add(c + 3);
                }
                mesh.SetTriangles(tris, (int)i, false);
                shown.Add(MaterialFor(view, batch.texture, batch.blend));
            }
            mesh.RecalculateBounds();
            renderer.sharedMaterials = shown.ToArray();
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
            foreach (var m in materials.Values) if (m) UnityEngine.Object.Destroy(m);
            foreach (var t in textures) if (t) UnityEngine.Object.Destroy(t);
        }
    }
}
