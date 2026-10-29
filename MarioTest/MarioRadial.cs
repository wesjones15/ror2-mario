using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // The nozzle picked by the radial menu, in the order the segments sit (clockwise from the
    // top). `None` takes FLUDD away; the native slots are 0 for none, then 1..4.
    internal enum MarioNozzleChoice
    {
        Spray = 0,
        Hover = 1,
        Rocket = 2,
        Turbo = 3,
        None = 4,
    }

    internal static class MarioNozzleChoices
    {
        /// The slot `sms_sim_select_nozzle` takes.
        public static uint NativeSlot(this MarioNozzleChoice choice) =>
            choice == MarioNozzleChoice.None ? 0u : (uint)choice + 1u;

        /// The choice the sim's state shows (Native.State.nozzle: 0 none, 1 Spray, 5 Hover,
        /// 2 Rocket, 6 Turbo).
        public static MarioNozzleChoice FromState(in Native.State state)
        {
            if (state.has_fludd == 0) return MarioNozzleChoice.None;
            switch (state.nozzle)
            {
                case 1: return MarioNozzleChoice.Spray;
                case 5: return MarioNozzleChoice.Hover;
                case 2: return MarioNozzleChoice.Rocket;
                case 6: return MarioNozzleChoice.Turbo;
                default: return MarioNozzleChoice.None;
            }
        }
    }

    // One segment of the radial menu. Nozzle entries are built in; other abilities can be
    // appended with MarioRadial.RegisterAbility, which is how the menu grows.
    internal sealed class MarioRadialEntry
    {
        public string Label;
        public Color Color;
        /// Set for FLUDD's nozzles (and No FLUDD); null for an extra ability.
        public MarioNozzleChoice? Nozzle;
        public Func<Texture2D> Icon;
        /// No FLUDD: draw the Spray picture with a red slash over it.
        public bool CrossOut;
        /// What releasing the open menu on this segment does. It runs inside the driver's step
        /// (so SimOps records it for the other players); null means the entry only reads state.
        public Action<MarioDriver> Apply;
    }

    // Hold the change-nozzle button to open, tilt the right stick (or move the mouse on a
    // keyboard) to choose the alternate nozzle, and push to full tilt to commit. A tap of the
    // button swaps between Spray and the chosen alternate; when the alternate is No FLUDD, a tap
    // on that empty button is the optional Melee Mario punch/kick. The wheel is anchored to the
    // Air Pump cooldown picture and overlays it with the chosen alternate. Built over IMGUI,
    // like FLUDD's tank, because the mod already presents its HUD there and needs no prefab.
    internal static class MarioRadial
    {
        static readonly List<MarioRadialEntry> entries = new List<MarioRadialEntry>();
        static readonly Color HoverColor = new Color(0.13f, 0.78f, 0.86f);
        static readonly Color RocketColor = new Color(0.96f, 0.48f, 0.13f);
        static readonly Color TurboColor = new Color(0.96f, 0.78f, 0.14f);
        static readonly Color NoneColor = new Color(0.55f, 0.56f, 0.62f);

        const float Deadzone = 0.32f;
        /// A full right-stick tilt commits at once; a partial tilt only shows the highlight.
        const float Commit = 0.9f;
        /// RB must be held this long before the wheel opens; a tap does nothing.
        const float OpenHold = 0.5f;
        const int RingSize = 512;

        static bool open, wasHeld, armed, tapPending;
        static float heldSince;
        static int hovered, builtinEntries;
        static MarioRadialEntry pending;
        static MarioNozzleChoice selectedAlt = MarioNozzleChoice.Hover;
        static Vector2 mouseAnchor;
        static IntPtr iconView;
        static Texture2D ring;
        static int ringHovered = -1, ringCount = -1;
        static GUIStyle label, centerLabel, titleStyle;
        static float styleHeight = -1f;

        static MarioRadial()
        {
            // Spray is the baseline and is reached by tapping, not from this wheel.
            AddNozzle("HOVER", HoverColor, MarioNozzleChoice.Hover, 1);
            AddNozzle("ROCKET", RocketColor, MarioNozzleChoice.Rocket, 2);
            AddNozzle("TURBO", TurboColor, MarioNozzleChoice.Turbo, 3);
            entries.Add(new MarioRadialEntry
            {
                Label = "NO FLUDD",
                Color = NoneColor,
                Nozzle = MarioNozzleChoice.None,
                // The HUD has no "off" picture: the Spray's, struck through.
                Icon = () => MarioNozzleIcons.Get(0, iconView),
                CrossOut = true,
                Apply = d => d.ChooseAlternative(MarioNozzleChoice.None),
            });
            builtinEntries = entries.Count;
        }

        static void AddNozzle(string label, Color color, MarioNozzleChoice choice, int icon)
        {
            entries.Add(new MarioRadialEntry
            {
                Label = label,
                Color = color,
                Nozzle = choice,
                Icon = () => MarioNozzleIcons.Get(icon, iconView),
                Apply = d => d.ChooseAlternative(choice),
            });
        }

        /// Appends an ability to the wheel. `apply` runs inside the driver's step when the
        /// segment is committed; `icon` may be null (a coloured label is drawn instead). Call
        /// before the first frame so every player sees the same segments.
        public static void RegisterAbility(string label, Color color, Func<Texture2D> icon,
            Action<MarioDriver> apply)
        {
            if (string.IsNullOrEmpty(label) || apply == null) return;
            entries.Add(new MarioRadialEntry { Label = label, Color = color, Icon = icon, Apply = apply });
            ringCount = -1;
        }

        /// The menu is up (a trigger pull while it is should not spray).
        public static bool Open => open;

        /// The alternate nozzle RB taps to, or No FLUDD for the empty button.
        public static MarioNozzleChoice SelectedAlt => selectedAlt;

        public static void SetSelectedAlt(MarioNozzleChoice choice) => selectedAlt = choice;

        /// Every frame for the local player: the button state and the stick. A release before
        /// the hold is a tap (Spray/alternate swap); `mouseFallback` lets a keyboard pick a
        /// segment by pointing at it.
        public static void Update(bool held, Vector2 direction, bool mouseFallback, IntPtr view)
        {
            iconView = view;
            bool letGo = wasHeld && !held;
            if (held && !wasHeld)
            {
                // RB only opens the wheel after a deliberate hold. A shorter press becomes a
                // tap, which the driver applies as the Spray/alternate swap.
                open = false;
                armed = false;
                heldSince = Time.unscaledTime;
            }
            wasHeld = held;
            if (!open)
            {
                if (!held)
                {
                    if (letGo && Time.unscaledTime - heldSince < OpenHold) tapPending = true;
                    return;
                }
                if (Time.unscaledTime - heldSince < OpenHold) return;
                open = true;
                // A stick already held over from the camera must be released before it counts:
                // pressing RB during a turn should not pick that direction immediately.
                armed = direction.sqrMagnitude < Commit * Commit;
                hovered = Mathf.Max(0, IndexOf(selectedAlt));
                mouseAnchor = Input.mousePosition;
            }
            if (!held)
            {
                // Releasing without a full-tilt choice cancels; the wheel commits on the
                // first full-tilt selection instead.
                open = false;
                armed = false;
                return;
            }
            var dir = direction;
            if (dir.sqrMagnitude < Deadzone * Deadzone && mouseFallback)
            {
                float h = Mathf.Min(Screen.width, Screen.height);
                dir = MouseDirection(WheelCentre(h * 0.18f, h));
            }
            if (dir.sqrMagnitude < 0.6f * 0.6f) armed = true;
            if (dir.sqrMagnitude < Deadzone * Deadzone) return;
            int at = SegmentAt(dir);
            if (at < 0) return;
            hovered = at;
            if (!armed || dir.sqrMagnitude < Commit * Commit) return;
            // Commit on the first full tilt and close at once; the step applies it.
            pending = entries[at];
            open = false;
            armed = false;
            wasHeld = true;
        }

        /// A short press: the driver swaps Spray and the selected alternate.
        public static bool TakeTap()
        {
            bool tap = tapPending;
            tapPending = false;
            return tap;
        }

        /// A segment was chosen as the button came up. The driver calls this inside its step.
        public static bool TakeChoice(out MarioRadialEntry entry)
        {
            entry = pending;
            pending = null;
            return entry != null;
        }

        /// Closes the menu without choosing (pause, UI focus, death, a new stage).
        public static void Cancel()
        {
            open = false;
            armed = false;
            pending = null;
            tapPending = false;
            wasHeld = true; // a held button must be released before the menu opens again
        }

        public static void Reset()
        {
            Cancel();
            wasHeld = false;
            selectedAlt = MarioNozzleChoice.Hover;
            hovered = -1;
            ringHovered = ringCount = -1;
            if (entries.Count > builtinEntries)
                entries.RemoveRange(builtinEntries, entries.Count - builtinEntries);
            MarioMeleeAttack.Reset();
            if (ring) UnityEngine.Object.Destroy(ring);
            ring = null;
            MarioNozzleIcons.Reset();
        }

        static int IndexOf(MarioNozzleChoice choice)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Nozzle == choice) return i;
            return -1;
        }

        static int SegmentAt(Vector2 direction)
        {
            if (entries.Count == 0) return -1;
            // 0 at the top, clockwise. The half-segment offset puts each segment's centre on
            // an axis, so up/right/down/left pick the four nozzles instead of the diagonals.
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            float step = 360f / entries.Count;
            float fromTop = Mathf.Repeat(90f - angle + step * 0.5f, 360f);
            int index = Mathf.FloorToInt(fromTop / step);
            return index >= 0 && index < entries.Count ? index : -1;
        }

        static Vector2 MouseDirection(Vector2 center)
        {
            var mouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            // Opening the wheel must not count the mouse's resting place as a choice: it has to
            // move once before it points at a segment.
            if ((mouse - mouseAnchor).sqrMagnitude < 16f * 16f) return Vector2.zero;
            var d = mouse - center;
            float reach = Mathf.Min(Screen.width, Screen.height) * 0.25f;
            return d.magnitude < 12f ? Vector2.zero : d.normalized * Mathf.Clamp01(d.magnitude / reach);
        }

        /// The Air Pump skill icon's screen rect (GUI coordinates), if the HUD is up.
        static bool TrySkillIcon(out Rect gui)
        {
            gui = default;
            var user = MarioPresentation.User;
            if (user == null) return false;
            foreach (var hud in RoR2.UI.HUD.readOnlyInstanceList)
            {
                if (!hud || hud.localUserViewer != user || hud.skillIcons == null
                    || hud.skillIcons.Length < 2)
                    continue;
                var part = hud.skillIcons[1];
                var rt = part ? part.transform as RectTransform : null;
                if (!rt) continue;
                var canvas = hud.mainContainerCanvas ? hud.mainContainerCanvas.rootCanvas : null;
                var camera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera : null;
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                float x0 = float.MaxValue, y0 = float.MaxValue;
                float x1 = float.MinValue, y1 = float.MinValue;
                foreach (var c in corners)
                {
                    var p = RectTransformUtility.WorldToScreenPoint(camera, c);
                    x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                    y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
                }
                if (x1 - x0 < 1f || y1 - y0 < 1f) continue;
                // WorldToScreenPoint has y up; IMGUI has y down.
                gui = Rect.MinMaxRect(x0, Screen.height - y1, x1, Screen.height - y0);
                return true;
            }
            return false;
        }

        /// The wheel's centre: just above the Air Pump icon, or the screen's middle without it.
        static Vector2 WheelCentre(float outer, float h)
        {
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (TrySkillIcon(out var icon))
                center = new Vector2(icon.center.x, icon.yMin - outer - h * 0.012f);
            float edge = h * 0.012f;
            center.x = Mathf.Clamp(center.x, outer + edge,
                Mathf.Max(outer + edge, Screen.width - outer - edge));
            center.y = Mathf.Clamp(center.y, outer + edge,
                Mathf.Max(outer + edge, Screen.height - outer - edge));
            return center;
        }

        /// From MarioPresentation.OnGUI: the chosen alternate's picture over the Air Pump icon.
        public static void DrawAltIcon()
        {
            if (selectedAlt == MarioNozzleChoice.None || Event.current.type != EventType.Repaint)
                return;
            if (PauseManager.isPaused || !RoR2.UI.HUD.cvHudEnable.value) return;
            var view = MarioView.Player;
            if (view == null || !TrySkillIcon(out var icon)) return;
            int index = selectedAlt == MarioNozzleChoice.Hover ? 1
                : selectedAlt == MarioNozzleChoice.Rocket ? 2
                : selectedAlt == MarioNozzleChoice.Turbo ? 3 : -1;
            var tex = MarioNozzleIcons.Get(index, view.Handle);
            if (!tex) return;
            float size = Mathf.Min(icon.width, icon.height) * 0.68f;
            var at = new Rect(icon.center.x - size * 0.5f, icon.center.y - size * 0.5f, size, size);
            var old = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(at, tex, ScaleMode.ScaleToFit, true);
            GUI.color = old;
        }

        /// From MarioPresentation.OnGUI, after the HUD.
        public static void Draw()
        {
            if (!open || entries.Count == 0 || Event.current.type != EventType.Repaint) return;
            if (PauseManager.isPaused || !RoR2.UI.HUD.cvHudEnable.value) return;
            float h = Mathf.Min(Screen.width, Screen.height);
            float outer = h * 0.18f, inner = outer * 0.57f;
            var center = WheelCentre(outer, h);
            EnsureStyles(h);
            EnsureRing();
            if (!ring) return;

            var old = GUI.color;
            // A quiet dim behind the wheel so the segments read on any stage.
            GUI.color = new Color(0f, 0f, 0f, 0.30f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(center.x - outer, center.y - outer, outer * 2f, outer * 2f),
                ring, ScaleMode.StretchToFill, true);

            float step = 360f / entries.Count;
            float iconSize = Mathf.Clamp(h * 0.055f, 28f, 72f);
            for (int i = 0; i < entries.Count; i++)
            {
                // Math coordinates here (y up); the screen is turned over below.
                float a = (90f - i * step) * Mathf.Deg2Rad;
                var radial = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var iconAt = center + new Vector2(radial.x, -radial.y) * (outer * 0.74f);
                var iconRect = new Rect(iconAt.x - iconSize * 0.5f, iconAt.y - iconSize * 0.5f,
                    iconSize, iconSize);
                // The nozzle picture turns with its segment, so its top points out of the wheel.
                DrawEntryIcon(entries[i], iconRect, iconSize, i * step);
                // Outside the ring, so a long label (NO FLUDD) cannot sit on its icon.
                var textAt = center + new Vector2(radial.x, -radial.y) * (outer * 1.04f);
                var textRect = new Rect(textAt.x - outer * 0.55f, textAt.y - h * 0.014f,
                    outer * 1.1f, h * 0.028f);
                Shadow(textRect, entries[i].Label, i == hovered ? titleStyle : label);
            }

            var at = entries[Mathf.Clamp(hovered, 0, entries.Count - 1)];
            float hub = Mathf.Clamp(h * 0.055f, 30f, 68f);
            var hubRect = new Rect(center.x - hub * 0.5f, center.y - hub * 0.5f - h * 0.006f, hub, hub);
            DrawEntryIcon(at, hubRect, hub, 0f);
            var nameRect = new Rect(center.x - inner, center.y + hub * 0.55f, inner * 2f, h * 0.030f);
            Shadow(nameRect, at.Label, centerLabel);
            var hintRect = new Rect(center.x - outer, center.y - outer - h * 0.052f,
                outer * 2f, h * 0.04f);
            Shadow(hintRect, "FULL RS TILT TO CHOOSE  -  RELEASE TO CANCEL", label);
            GUI.color = old;
        }

        static void DrawEntryIcon(MarioRadialEntry entry, Rect rect, float size, float rotation)
        {
            var icon = entry.Icon != null ? entry.Icon() : null;
            if (!icon)
            {
                GUI.color = entry.Color;
                GUI.DrawTexture(new Rect(rect.x + size * 0.12f, rect.y + size * 0.12f,
                    size * 0.76f, size * 0.76f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                Shadow(rect, entry.Label.Substring(0, 1), centerLabel);
                return;
            }
            var pivot = rect.center;
            var matrix = GUI.matrix;
            GUI.color = Color.white;
            if (rotation != 0f) GUIUtility.RotateAroundPivot(rotation, pivot);
            GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit, true);
            if (entry.CrossOut)
            {
                float length = size * 0.95f, thickness = Mathf.Max(3f, size * 0.09f);
                GUI.color = new Color(0.95f, 0.12f, 0.12f, 0.95f);
                GUIUtility.RotateAroundPivot(-45f, pivot);
                GUI.DrawTexture(new Rect(pivot.x - length * 0.5f, pivot.y - thickness * 0.5f,
                    length, thickness), Texture2D.whiteTexture);
            }
            GUI.matrix = matrix;
        }

        static void Shadow(Rect rect, string text, GUIStyle style)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, style);
            GUI.color = old;
            GUI.Label(rect, text, style);
        }

        static void EnsureStyles(float h)
        {
            if (styleHeight == h && label != null) return;
            styleHeight = h;
            label = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.RoundToInt(Mathf.Clamp(h * 0.017f, 12f, 28f)),
                fontStyle = FontStyle.Bold,
            };
            label.normal.textColor = new Color(0.90f, 0.94f, 1f, 0.88f);
            titleStyle = new GUIStyle(label)
            {
                fontSize = Mathf.RoundToInt(Mathf.Clamp(h * 0.021f, 14f, 34f)),
            };
            titleStyle.normal.textColor = Color.white;
            centerLabel = new GUIStyle(titleStyle) { alignment = TextAnchor.MiddleCenter };
        }

        static void EnsureRing()
        {
            if (ring && ringHovered == hovered && ringCount == entries.Count) return;
            ringHovered = hovered;
            ringCount = entries.Count;
            if (ring) UnityEngine.Object.Destroy(ring);
            ring = BuildRing();
        }

        static Texture2D BuildRing()
        {
            int n = entries.Count;
            if (n == 0) return null;
            var px = new Color32[RingSize * RingSize];
            float c = (RingSize - 1) * 0.5f;
            float outer = RingSize * 0.482f, inner = outer * 0.57f;
            float step = 360f / n;
            for (int y = 0; y < RingSize; y++)
            {
                float dy = y - c;
                for (int x = 0; x < RingSize; x++)
                {
                    float dx = x - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    Color32 pixel = default;
                    if (r <= inner)
                    {
                        // Hub, with a brighter rim at its edge.
                        bool rim = r > inner - 3f;
                        pixel = rim ? new Color(0.55f, 0.70f, 0.92f, 0.55f)
                            : new Color(0.035f, 0.055f, 0.085f, 0.82f);
                    }
                    else if (r <= outer + 1f)
                    {
                        float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                        // Centre each segment on a cardinal axis (up/right/down/left), matching
                        // the icons and SegmentAt's half-segment offset.
                        float fromTop = Mathf.Repeat(90f - angle + step * 0.5f, 360f);
                        int index = Mathf.Min(n - 1, Mathf.FloorToInt(fromTop / step));
                        float frac = (fromTop - index * step) / step;
                        // Gaps between the segments, and a dark outline at both edges.
                        if (frac > 0.018f && frac < 0.982f)
                        {
                            var entry = entries[index];
                            float lit = index == hovered ? 1f : 0.58f;
                            var col = Color.Lerp(new Color(0.04f, 0.05f, 0.08f), entry.Color, lit);
                            float alpha = index == hovered ? 0.94f : 0.78f;
                            if (r > outer - 2.5f || r < inner + 2f)
                            {
                                col = Color.Lerp(col, Color.white, index == hovered ? 0.55f : 0.18f);
                                alpha = 0.9f;
                            }
                            pixel = new Color(col.r, col.g, col.b, alpha);
                        }
                    }
                    px[y * RingSize + x] = pixel;
                }
            }
            var tex = new Texture2D(RingSize, RingSize, TextureFormat.RGBA32, false)
            {
                name = "FLUDD radial menu",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }
    }
}
