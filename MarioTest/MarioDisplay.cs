using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // Mario on the character select screen (ror2-common's DisplayModel): his own sim standing
    // idle with FLUDD on a flat floor at the display's root (so he breathes, looks about and
    // blinks), drawn through his own silent view. Views take a moment to build (the bakes), so
    // released ones wait in a pool for the next lobby.
    internal sealed class MarioDisplay : IDisplayActor
    {
        static readonly Stack<MarioDisplay> spare = new Stack<MarioDisplay>();
        static readonly Native.GroundFn floorFn = Floor;
        static Native.Host floor = new Native.Host { ground = Marshal.GetFunctionPointerForDelegate(floorFn) };

        readonly IntPtr view;
        IntPtr sim;
        public ModelView Models { get; }
        public float Scale => UnityHost.Scale;

        MarioDisplay(IntPtr view, IntPtr sim, ModelView models)
        {
            this.view = view;
            this.sim = sim;
            Models = models;
        }

        /// A display Mario, or null (the prefab keeps Commando) if the disc's files won't load.
        public static IDisplayActor Get()
        {
            if (spare.Count > 0)
            {
                var d = spare.Pop();
                var origin = new Native.Vec3(0f, 0f, 0f);
                Native.sms_sim_teleport(d.sim, ref origin);
                return d;
            }
            var start = new Native.Vec3(0f, 0f, 0f);
            var view = Native.sms_view_new(NativeLib.CString(Plugin.AssetsDir), 0);
            if (view == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Mario's lobby model didn't load; showing Commando");
                return null;
            }
            var sim = Native.sms_sim_new(NativeLib.CString(Plugin.AssetsDir), ref start, 0);
            var models = sim != IntPtr.Zero ? ModelView.Create(new SmsViewSource(view), "Mario (lobby)") : null;
            if (models == null)
            {
                if (sim != IntPtr.Zero) Native.sms_sim_free(sim);
                Native.sms_view_free(view);
                return null;
            }
            return new MarioDisplay(view, sim, models);
        }

        public void Step(float dt, Transform display)
        {
            var pad = default(Native.Pad);
            Native.sms_sim_step(sim, ref floor, ref pad, short.MinValue, dt, view);
            var frame = new Native.ViewFrame
            {
                dt = dt,
                forward = new Native.Vec3(0f, 0f, 1f),
                up = new Native.Vec3(0f, 1f, 0f),
            };
            // The camera in the display's space, for the shiny parts' reflections.
            var cam = Camera.main;
            if (cam && display)
            {
                var inv = Quaternion.Inverse(display.rotation);
                var f = inv * cam.transform.forward;
                var u = inv * cam.transform.up;
                var e = UnityHost.ToSim(inv * (cam.transform.position - display.position));
                frame.eye = e;
                frame.forward = new Native.Vec3(f.x, f.y, f.z);
                frame.up = new Native.Vec3(u.x, u.y, u.z);
            }
            Native.sms_view_update(view, sim, ref frame);
        }

        public void Release()
        {
            Models.HideAll();
            spare.Push(this);
        }

        static byte Floor(IntPtr user, ref Native.Vec3 pos, out Native.GroundHit hit)
        {
            hit = default;
            hit.normal = new Native.Vec3(0f, 1f, 0f);
            return 1;
        }
    }
}
