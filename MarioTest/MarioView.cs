using System;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // A Mario's look, loaded from the disc's files: libsms's view (sms_view_*: the models posed
    // and skinned, each material's TEV baked into one texture, and the game's sounds on our own
    // output, since RoR2 plays through Wwise), ror2-common's ModelView putting it in the scene,
    // FLUDD's water (MarioWater) and the game's particle effects (MarioFx).
    //
    // The local player's Mario has the one with sound and the HUD's water gauge (MarioHud),
    // made the first time it's needed and kept across stages (Player). A Mario no player
    // controls (Goobo Jr.'s clone) gets its own, silent and in RoR2's gummy clone material
    // (Clone). A Mario who dies hands his to MarioCorpse, which plays out the fall.
    internal sealed class MarioView : IDisposable
    {
        static bool tried;
        static MarioView player;

        IntPtr view;
        ModelView models;
        MarioWater water;
        MarioFx fx;
        readonly bool hud;
        readonly ViewDraw[] firstDraw = new ViewDraw[1];

        /// The local player's, or null before Ensure (or after a failed load).
        public static MarioView Player => player;

        /// The native view (for sms_sim_step's sounds and sms_view_update), or zero.
        public IntPtr Handle => view;
        public ModelView Models => models;
        /// The game's own particles are drawn (else MarioFlames stands in).
        public bool HasEffects => fx != null;

        MarioView(IntPtr view, bool hud, string name, Material skin = null)
        {
            this.view = view;
            this.hud = hud;
            if (!Plugin.DrawModel.Value) return;
            models = ModelView.Create(new SmsViewSource(view), name, null, skin);
            // Sunshine's hit flash is pink (`addDamageFog`).
            if (models != null) models.FlashColor = new Color(1f, 0f, 0.5f, 0.6f);
            water = MarioWater.Create(view);
            fx = MarioFx.Create(view);
        }

        /// The player's view, made the first time.
        public static MarioView Ensure()
        {
            if (!Plugin.AssetsReady || RunAudio.Ended) return null;
            if (tried) return player;
            tried = true;
            if (!Plugin.DrawModel.Value && !Plugin.Sound.Value) return null;
            var v = Open(Plugin.Sound.Value, 0);
            if (v == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"Mario's models didn't load from {Plugin.AssetsDir}; he stays Commando");
                return null;
            }
            Plugin.Log.LogInfo($"Mario's view: {NativeLib.Utf8(Native.sms_view_status(v))}");
            player = new MarioView(v, true, "Mario");
            return player;
        }

        /// A silent view in RoR2's gummy clone material (Mario's own colours if that won't load),
        /// for a Mario no player controls; null if the view can't load or drawing is off.
        public static MarioView Clone()
        {
            if (!Plugin.AssetsReady || RunAudio.Ended || !Plugin.DrawModel.Value || !Plugin.GummyClones.Value) return null;
            var v = Open(false, 0);
            if (v == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("The clone's view didn't load; the clone stays Commando");
                return null;
            }
            return new MarioView(v, false, "GooboMario", GummyClone.Material());
        }

        /// Another player's Mario (a puppet): his own view, with his sounds from where he is,
        /// or silent in the gummy clone material if it's a Mario no player controls. Null if
        /// drawing is off or the view can't load.
        public static MarioView Remote(bool clone)
        {
            if (!Plugin.AssetsReady || RunAudio.Ended || !Plugin.DrawModel.Value) return null;
            if (clone && !Plugin.GummyClones.Value) return null;
            var v = Open(Plugin.Sound.Value && !clone, 0);
            if (v == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Another player's Mario view didn't load; he stays Commando");
                return null;
            }
            return new MarioView(v, false, clone ? "GooboMario" : "RemoteMario", clone ? GummyClone.Material() : null);
        }

        static IntPtr Open(bool sound, byte look)
        {
            try
            {
                return Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), (byte)(sound ? 1 : 0), look);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Mario's view: {e}");
                return IntPtr.Zero;
            }
        }

        /// Takes the player's view away from the player slot (a dying Mario's, for his corpse):
        /// the next Ensure makes a new one.
        public static MarioView TakePlayer()
        {
            var v = player;
            player = null;
            tried = false;
            return v;
        }

        /// Poses `sim` for this frame, draws its water, and plays its sounds from the player's
        /// camera. `lag` is how many 120 Hz ticks behind the sim Mario is drawn.
        public void Update(IntPtr sim, float lag)
        {
            if (view == IntPtr.Zero || sim == IntPtr.Zero) return;
            var frame = new Native.ViewFrame
            {
                dt = Time.deltaTime,
                volume = GameSettings.SfxVolume() * Mathf.Clamp(Plugin.SoundVolume.Value, 0f, 2f),
                lag = lag,
                forward = new Native.Vec3(0f, 0f, 1f),
                up = new Native.Vec3(0f, 1f, 0f),
            };
            var cam = GameSettings.PlayerCamera();
            if (cam)
            {
                frame.eye = UnityHost.ToSim(cam.position);
                frame.forward = new Native.Vec3(cam.forward.x, cam.forward.y, cam.forward.z);
                frame.up = new Native.Vec3(cam.up.x, cam.up.y, cam.up.z);
            }
            Native.sms_view_set_fx(view, (byte)(Plugin.Effects.Value ? 1 : 0),
                (byte)(Plugin.PoundFlames.Value ? 1 : 0));
            Native.sms_view_set_smoke_speed(view, Plugin.SmokeSpeed.Value);
            Native.sms_view_update(view, sim, ref frame);
            water?.Update(view, UnityHost.Scale);
            if (hud) MarioHud.Update(view);
        }

        /// Draws this frame's particle effects (after Update) with Mario drawn at `feet`: they
        /// move with him from where the sim has him to where he's drawn.
        public void Effects(Vector3 feet)
        {
            if (fx == null || view == IntPtr.Zero) return;
            var offset = Vector3.zero;
            if (Native.sms_view_draws(view, firstDraw, 1) > 0)
            {
                var p = firstDraw[0].pos;
                offset = feet - new Vector3(p.x, p.y, p.z) * UnityHost.Scale;
            }
            fx.Update(view, UnityHost.Scale, offset);
        }

        /// Poses the models at `feet` (after Update).
        public void Draw(Vector3 feet)
        {
            models?.Update(UnityHost.Scale, d => (feet, new Quaternion(d.rot_x, d.rot_y, d.rot_z, d.rot_w)));
        }

        /// Hides the models, the water and the effects (until the next Update).
        public void Hide()
        {
            models?.HideAll();
            water?.Hide();
            fx?.Hide();
        }

        public void Dispose()
        {
            // Free the stream first: its audio thread otherwise keeps rendering held
            // voices after sms_sim_step stops advancing their release timers.
            var old = view;
            view = IntPtr.Zero;
            if (old != IntPtr.Zero) Native.sms_view_free(old);
            models?.Dispose(); models = null;
            water?.Dispose(); water = null;
            fx?.Dispose(); fx = null;
            if (hud) MarioHud.Reset();
        }

        /// Frees the player's view (run over, or the player's Mario gone); the next Ensure
        /// makes it again.
        public static void Reset()
        {
            var v = player;
            player = null;
            v?.Dispose();
            MarioHud.Reset();
            MarioLife.Reset();
            tried = false;
        }
    }
}
