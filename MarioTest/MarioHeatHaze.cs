using System;
using Ror2Common;
using UnityEngine;
using UnityEngine.Rendering;

namespace MarioTest
{
    // TShimmer: a camera-facing depth-tested sheet refracting only scenery behind
    // its authored 2425-unit depth. The source BMD normal map and BTK UVs come from Rust.
    internal static class MarioHeatHaze
    {
        static IntPtr native;
        static Mesh mesh;
        static Texture2D texture;
        static Material material;
        static bool hooked, tried;
        static readonly float[] frame = new float[8];
        static readonly Vector3[] positions = new Vector3[4];
        static readonly Vector2[] uvs = new Vector2[4];
        // Sunshine's sheet is at 2425 units (24.25 m); RoR2's camera sits farther back,
        // so that depth reads as mid-ground here. Push the sheet out without weakening
        // the screen-space shimmer (the magnitude below uses the same distance).
        const float DepthScale = 1.5f;
        // The same authored offsets read stronger on RoR2's sharper, larger image;
        // keep the pattern at a fraction of its literal screen displacement.
        const float MagnitudeScale = 0.65f;
        static readonly Vector2[] sourceUvs = { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) };

        internal static void Initialize()
        {
            if (hooked) return;
            hooked = true;
            Camera.onPreCull += Draw;
        }

        static bool Ensure()
        {
            if (tried) return native != IntPtr.Zero && mesh && material;
            tried = true;
            native = Native.sms_heat_new(NativeLib.CString(Plugin.AssetsDir));
            if (native == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Sunshine heat haze is unavailable: extract ShimmerLow from the Sunshine disc.");
                return false;
            }
            if (Native.sms_heat_texture(native, out var info, null, 0) == 0) return false;
            var pixels = new byte[info.width * info.height * 4];
            if (Native.sms_heat_texture(native, out info, pixels, (uint)pixels.Length) == 0
                || Native.sms_heat_frame(native, 0f, frame, 8) == 0) return false;
            // This is vector data: sRGB conversion would bias neutral normals
            // and turn a subtle haze into a constant screen displacement.
            texture = new Texture2D((int)info.width, (int)info.height, TextureFormat.RGBA32, false, true)
            { name = "Sunshine B_kagero_s refraction", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat };
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, true);
            // Separate from WaterRefraction: that setting only controls FLUDD.
            // The shader adds the offset to grab-screen position before dividing by
            // clip w, so its effective UV offset is normal * _Magnitude / distance.
            // The reciprocal gain alone leaves the sheet ~24x too weak (sub-pixel);
            // include the sheet distance to restore the source's screen offset.
            material = MarioWater.Refraction("Sunshine heat haze", texture, Texture2D.whiteTexture, frame[1],
                frame[0] * UnityHost.Scale * DepthScale * MagnitudeScale / frame[1]);
            if (!material) return false;
            material.SetTextureScale("_BumpMap", Vector2.one);
            material.SetTextureOffset("_BumpMap", Vector2.zero);
            foreach (var property in new[] { "_ZTest", "_ZTestMode" })
                if (material.HasProperty(property)) material.SetFloat(property, (float)CompareFunction.LessEqual);
            mesh = new Mesh { name = "Sunshine distant heat-haze sheet" };
            mesh.vertices = new Vector3[4];
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.tangents = new[] { new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) };
            mesh.uv = sourceUvs;
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            return true;
        }

        static void Draw(Camera camera)
        {
            // This deliberately follows the owner's camera rather than a live Mario body.
            if (!camera || !Plugin.HeatHaze.Value || !Plugin.AssetsReady || !MarioPresentation.Active
                || camera != MarioPresentation.Camera || Ror2Common.GameSettings.CameraOverride) return;
            // In a headset the sheet would ride the head, distorting the view as it turns.
            if (Ror2Common.Vr.Active && !Plugin.VrHeatHaze.Value) return;
            if (!Ensure() || Native.sms_heat_frame(native, Time.time, frame, 8) == 0) return;
            float distance = frame[0] * UnityHost.Scale * DepthScale;
            if (distance <= camera.nearClipPlane || distance >= camera.farClipPlane) return;
            float halfY = camera.orthographic ? camera.orthographicSize : Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * distance;
            float halfX = halfY * camera.aspect;
            positions[0] = new Vector3(-halfX, -halfY, 0); positions[1] = new Vector3(halfX, -halfY, 0);
            positions[2] = new Vector3(halfX, halfY, 0); positions[3] = new Vector3(-halfX, halfY, 0);
            for (int i = 0; i < 4; i++)
            {
                var uv = sourceUvs[i];
                uvs[i] = new Vector2(frame[2] * uv.x + frame[3] * uv.y + frame[4], frame[5] * uv.x + frame[6] * uv.y + frame[7]);
            }
            mesh.vertices = positions; mesh.uv = uvs; mesh.RecalculateBounds();
            Graphics.DrawMesh(mesh, Matrix4x4.TRS(camera.transform.position + camera.transform.forward * distance, camera.transform.rotation, Vector3.one),
                material, 0, camera, 0, null, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
        }

        internal static void Shutdown()
        {
            if (hooked) Camera.onPreCull -= Draw;
            hooked = false;
            if (native != IntPtr.Zero) Native.sms_heat_free(native);
            native = IntPtr.Zero;
            if (mesh) UnityEngine.Object.Destroy(mesh);
            if (material) UnityEngine.Object.Destroy(material);
            if (texture) UnityEngine.Object.Destroy(texture);
            mesh = null; material = null; texture = null; tried = false;
        }
    }
}
