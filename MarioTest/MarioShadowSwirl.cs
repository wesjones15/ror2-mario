using System;
using Ror2Common;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;

namespace MarioTest
{
    // Shadow Mario's swirl (libsms-present shadow_skin.rs; TEMario::init, emario.cpp). He is
    // opaque: every kagemario material is blend mode 0 (no blending) with z write on, and its
    // first TEV stage only uses the warped screen copy as the factor of lerp(REG0, REG1, screen),
    // a navy (28,20,93) to pale blue (157,194,255) ramp; the second puts his features in REG2's
    // navy through H_kagemario_main and adds the lit colour. So he is solid blue with a moving
    // light-and-dark swirl over him, never the scene showing through. The native view bakes that
    // onto his body (the disc's warp standing in for the screen copy) and the body draws opaque
    // with it. This pass then draws the body again with RoR2's screen-refraction material
    // (matDistortion, as the heat haze and FLUDD's water use it), its bump map the disc's warp
    // re-scrolled each game frame (sms_view_shadow_refraction). It runs after the opaque body,
    // so what it warps is mostly his own swirl: the shimmering layer on top of the blue.
    internal sealed class MarioShadowSwirl : IDisposable
    {
        const string DistortionKey = "RoR2/Base/Common/VFX/matDistortion.mat";

        ModelView models;
        Texture2D texture;
        uint bodyModel;
        byte[] pixels;
        uint version;

        /// Null unless `view` draws Shadow Mario's skin (or the refraction material won't load).
        public static MarioShadowSwirl Create(IntPtr view, string name)
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
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            // Only where his body is in front (his own opaque body included): no warp through walls.
            foreach (var property in new[] { "_ZTest", "_ZTestMode" })
                if (material.HasProperty(property)) material.SetFloat(property, (float)CompareFunction.LessEqual);
            var models = ModelView.Create(new SmsViewSource(view), name + " swirl", null, material);
            UnityEngine.Object.Destroy(material);
            if (models == null)
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            Plugin.Log.LogInfo($"Shadow Mario's swirl: model {bodyModel}, opaque body, refraction layer over it ({info.width}x{info.height})");
            return new MarioShadowSwirl { models = models, texture = tex, bodyModel = bodyModel, pixels = px, version = version };
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

        /// Over the body only (the brush keeps its own look).
        public void Draw(Func<ViewDraw, (Vector3, Quaternion)?> placement, Func<ViewDraw, bool> hide) =>
            models?.Update(UnityHost.Scale, placement, draw => draw.model != bodyModel || hide?.Invoke(draw) == true);

        public void Hide() => models?.HideAll();

        public void Dispose()
        {
            models?.Dispose();
            models = null;
            if (texture) UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }
}
