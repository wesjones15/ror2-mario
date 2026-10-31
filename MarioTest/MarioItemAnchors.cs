using System;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    /// Mario's body attachment points, read from the native view's posed skin rig. The
    /// view maps them per skin (Mario/Piantissimo's Sunshine rig, Luigi's Melee rig,
    /// Yoshi's own rig), so item displays follow whichever body is drawn.
    internal sealed class MarioItemAnchors : IItemAnchorSource
    {
        readonly MarioView view;
        readonly float[] matrix = new float[16];

        public MarioItemAnchors(MarioView view) { this.view = view; }

        public void BeginFrame() { }

        public bool TryLocalPosition(ItemAnchor anchor, out Vector3 local)
        {
            local = default;
            if (view == null || view.Handle == IntPtr.Zero) return false;
            try
            {
                if (Native.sms_view_anchor(view.Handle, (uint)anchor, matrix) != 1) return false;
            }
            catch (Exception) { return false; }
            local = new Vector3(matrix[12], matrix[13], matrix[14]);
            return true;
        }
    }
}
