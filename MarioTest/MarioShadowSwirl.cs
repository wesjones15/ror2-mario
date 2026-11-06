using System;
using Ror2Common;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace MarioTest
{
    // Shadow Mario's screen warp (libsms-present shadow_skin.rs; TEMario::init, emario.cpp). The
    // game draws him from a copy of the screen pushed about by H_kagemario_indwp4n through the
    // texture matrix kagemario_scroll.btk scrolls, so he is a moving warp of whatever is behind
    // him. RoR2 captures the scene for matDistortion, then blends the native blue bake over the
    // warped scene. The body's tint draws in the transparent queue so the scene grab and depth
    // texture are clear of the body. This uses body UVs for the disc's scrolling warp.
    internal sealed class MarioShadowSwirl : IDisposable
    {
        const string DistortionKey = "RoR2/Base/Common/VFX/matDistortion.mat";

        ModelView models;
        Texture2D texture;
        Material tint;
        uint bodyModel;
        byte[] pixels;
        uint version;

        /// Null unless `view` draws Shadow Mario's skin (or the refraction material won't load).
        public static MarioShadowSwirl Create(IntPtr view, ModelView body, string name)
        {
            float strength = KoopaJrGoop.SwirlRefraction != null ? KoopaJrGoop.SwirlRefraction.Value : 1f;
            if (view == IntPtr.Zero || strength <= 0f || !Plugin.DrawModel.Value) return null;
            if (Native.sms_view_shadow_refraction(view, out var info, null, 0, out float gain) == 0) return null;
            var px = new byte[info.width * info.height * 4];
            uint version = Native.sms_view_shadow_refraction(view, out info, px, (uint)px.Length, out gain);
            if (version == 0) return null;
            // view.rs submits Shadow Mario's rig first, before his brush and any accessories.
            var first = new ViewDraw[1];
            if (Native.sms_view_draws(view, first, 1) == 0) return null;
            uint bodyModel = first[0].model;
            var baked = body.FirstBatchTexture(bodyModel);
            if (!baked) return null;
            var tint = Shaders.SeeThrough(name + " tint", baked, additive: false, vertexColor: false, opacity: 0.42f);
            if (!tint) return null;
            // Vector data: no sRGB conversion. Kept readable for the per-frame upload.
            var tex = new Texture2D((int)info.width, (int)info.height, TextureFormat.RGBA32, false, true)
            {
                name = "Shadow Mario indwp4n refraction",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
            };
            tex.SetPixelData(px, 0);
            tex.Apply(false, false);
            float baseMagnitude = 1f;
            try
            {
                var src = Addressables.LoadAssetAsync<Material>(DistortionKey).WaitForCompletion();
                if (src && src.HasProperty("_Magnitude")) baseMagnitude = src.GetFloat("_Magnitude");
            }
            catch (Exception) { }
            var material = MarioWater.Refraction("Shadow Mario swirl", tex, Texture2D.whiteTexture, gain,
                baseMagnitude * strength / Mathf.Max(0.01f, gain));
            if (!material)
            {
                UnityEngine.Object.Destroy(tint);
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            // The shader depth-tests against the scene. Keep its named grab in the late queue,
            // just ahead of the blue tint that samples the native animated texture.
            tint.renderQueue = material.renderQueue;
            material.renderQueue = tint.renderQueue - 1;
            var models = ModelView.Create(new SmsViewSource(view), name + " swirl", null, material);
            UnityEngine.Object.Destroy(material);
            if (models == null)
            {
                UnityEngine.Object.Destroy(tint);
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            Plugin.Log.LogInfo($"Shadow Mario's swirl: model {bodyModel}, scene refraction then translucent tint ({info.width}x{info.height})");
            return new MarioShadowSwirl { models = models, texture = tex, tint = tint, bodyModel = bodyModel, pixels = px, version = version };
        }

        /// The warp at this game frame (after sms_view_update).
        public void Update(IntPtr view)
        {
            if (view == IntPtr.Zero || !texture) return;
            uint v = Native.sms_view_shadow_refraction(view, out _, pixels, (uint)pixels.Length, out _);
            if (v == 0 || v == version) return;
            version = v;
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);
        }

        // A separate tint for the body leaves the brush on its own authored material.
        public Material BodyTint(ViewDraw draw) => draw.model == bodyModel ? tint : null;

        public void Draw(Func<ViewDraw, (Vector3, Quaternion)?> placement, Func<ViewDraw, bool> hide) =>
            models?.Update(UnityHost.Scale, placement, draw => draw.model != bodyModel || hide?.Invoke(draw) == true);

        public void Hide() => models?.HideAll();

        public void Dispose()
        {
            models?.Dispose();
            models = null;
            if (texture) UnityEngine.Object.Destroy(texture);
            texture = null;
            if (tint) UnityEngine.Object.Destroy(tint);
            tint = null;
        }
    }
}
