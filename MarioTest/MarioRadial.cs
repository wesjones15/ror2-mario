using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace MarioTest
{
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
    // on that empty button is the optional Melee Mario punch/kick. The wheel emerges from
    // the HUD nozzle head beside the water tank; the cooldown shows the chosen alternate. Built over IMGUI,
    // like FLUDD's tank, because the mod already presents its HUD there and needs no prefab.
    internal static class MarioRadial
    {
        static readonly List<MarioRadialEntry> entries = new List<MarioRadialEntry>();
        static readonly Color HoverColor = new Color(0.13f, 0.78f, 0.86f);
        static readonly Color RocketColor = new Color(0.96f, 0.48f, 0.13f);
        static readonly Color TurboColor = new Color(0.96f, 0.78f, 0.14f);
        static readonly Color NoneColor = new Color(0.55f, 0.56f, 0.62f);
        // Item.cpp TNozzleBox::receiveMessage opens the box with system SE 0x3801.
        static readonly byte[] OpenSound = Ror2Common.NativeLib.CString("MSD_SE_IT_EGG_APPEAR");

        const float Deadzone = 0.32f;
        /// A full right-stick tilt commits at once; a partial tilt only shows the highlight.
        const float Commit = 0.9f;
        /// RB must be held this long before the wheel opens; a tap does nothing.
        const float OpenHold = 0.5f;
        const int RingSize = 512;
        /// The wheel grows out of the tank's nozzle head over this long (unscaled seconds).
        const float EmergeTime = 0.28f;
        // Cooldown overlay is the reference: the wide Hover pair gets the same extra
        // scale in the cooldown, each radial option, and the center preview.
        static float IconScale(MarioNozzleChoice? nozzle) => nozzle == MarioNozzleChoice.Hover ? 1.35f : 1f;

        static bool open, wasHeld, armed, tapPending;
        // This RB press has already opened the wheel (or been cancelled). It must be released
        // before another hold can open the wheel again.
        static bool pressSpent;
        static float heldSince, openedAt;
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
            AddNozzle("TURBO", TurboColor, MarioNozzleChoice.Turbo, 3);
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
                pressSpent = false;
                heldSince = Time.unscaledTime;
            }
            wasHeld = held;
            if (!open)
            {
                if (!held)
                {
                    // Only a press too short to open the wheel is a tap. A hold that opened or
                    // chose a segment never swaps on release.
                    if (letGo && !pressSpent && Time.unscaledTime - heldSince < OpenHold)
                        tapPending = true;
                    return;
                }
                // Once this hold has shown the wheel, releasing is the only way back in: a
                // choice closes it while RB is still down and it must stay closed.
                if (pressSpent) return;
                if (Time.unscaledTime - heldSince < OpenHold) return;
                open = true;
                pressSpent = true;
                openedAt = Time.unscaledTime;
                var soundView = MarioView.Player;
                if (soundView != null)
                {
                    var camera = Ror2Common.GameSettings.PlayerCamera();
                    var soundPosition = camera ? UnityHost.ToSim(camera.position) : default;
                    Native.sms_view_sound(soundView.Handle, OpenSound, ref soundPosition);
                }
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
            // Commit on the first full tilt and close at once; the step applies it. The press
            // stays spent, so the wheel does not pop back up while RB is still down.
            pending = entries[at];
            open = false;
            armed = false;
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
            // A held button must be released before the menu opens again, and a cancelled
            // press must not become a tap on release.
            pressSpent = true;
        }

        public static void Reset()
        {
            Cancel();
            wasHeld = false;
            pressSpent = false;
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

        /// The wheel direction (y up, as the stick's) for a point in GUI coordinates: the VR
        /// pointer on the wrist panel. Pointing over a segment's band is a full tilt; zero near
        /// the middle.
        public static Vector2 PointDirection(Vector2 gui)
        {
            float h = Mathf.Min(Screen.width, Screen.height);
            float outer = h * 0.18f;
            var d = gui - WheelCentre(outer, h);
            d.y = -d.y;
            float reach = outer * 0.8f;
            return d.magnitude < outer * 0.15f ? Vector2.zero : d.normalized * Mathf.Clamp01(d.magnitude / reach);
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

        /// To the left of FLUDD's tank, slightly above its nozzle head.
        static Vector2 WheelCentre(float outer, float h)
        {
            var center = new Vector2(Screen.width - outer * 2.2f, Screen.height - outer * 1.4f);
            if (MarioHud.TryNozzleHead(out var head, out var tank))
                center = new Vector2(tank.xMin - outer - h * 0.035f, head.center.y - outer * 0.6f);
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
            float size = Mathf.Min(icon.width, icon.height) * 0.95f * IconScale(selectedAlt);
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
            float progress = Mathf.Clamp01((Time.unscaledTime - openedAt) / EmergeTime);
            float grow = 1f - (1f - progress) * (1f - progress) * (1f - progress);
            GUI.color = new Color(0f, 0f, 0f, 0.30f * grow);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            // Lift a small wheel from the tank's actual nozzle head into position.
            var matrixBefore = GUI.matrix;
            if (grow < 1f)
            {
                var origin = MarioHud.TryNozzleHead(out var head, out _) ? head.center : center;
                float travel = progress * progress * (3f - 2f * progress);
                var position = Vector2.Lerp(origin, center, travel);
                float scale = Mathf.Lerp(0.08f, 1f, grow);
                GUI.matrix = matrixBefore * Matrix4x4.TRS(new Vector3(position.x, position.y, 0f),
                    Quaternion.identity, new Vector3(scale, scale, 1f))
                    * Matrix4x4.Translate(new Vector3(-center.x, -center.y, 0f));
            }
            GUI.DrawTexture(new Rect(center.x - outer, center.y - outer, outer * 2f, outer * 2f),
                ring, ScaleMode.StretchToFill, true);

            float step = 360f / entries.Count;
            float iconSize = Mathf.Clamp(h * 0.08f, 40f, 104f);
            for (int i = 0; i < entries.Count; i++)
            {
                // Math coordinates here (y up); the screen is turned over below.
                float a = (90f - i * step) * Mathf.Deg2Rad;
                var radial = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                // The left, right and bottom pictures sit slightly farther out so their centres
                // read as their segments' centres; the enlarged Hover picture already does.
                float reach = i == 0 ? outer * 0.74f : outer * 0.79f;
                var iconAt = center + new Vector2(radial.x, -radial.y) * reach;
                var iconRect = new Rect(iconAt.x - iconSize * 0.5f, iconAt.y - iconSize * 0.5f,
                    iconSize, iconSize);
                DrawEntryIcon(entries[i], iconRect, iconSize);
            }

            var at = entries[Mathf.Clamp(hovered, 0, entries.Count - 1)];
            float hub = Mathf.Clamp(h * 0.09f, 44f, 116f);
            var hubRect = new Rect(center.x - hub * 0.5f, center.y - hub * 0.5f - h * 0.012f, hub, hub);
            DrawEntryIcon(at, hubRect, hub);
            var nameRect = new Rect(center.x - inner, center.y + hub * 0.55f, inner * 2f, h * 0.030f);
            Shadow(nameRect, at.Label, centerLabel);
            var hintRect = new Rect(center.x - outer, center.y - outer - h * 0.052f,
                outer * 2f, h * 0.04f);
            Shadow(hintRect, "Select F.L.U.D.D. Nozzle with RS", label);
            GUI.color = old;
            GUI.matrix = matrixBefore;
        }

        static void DrawEntryIcon(MarioRadialEntry entry, Rect rect, float size)
        {
            float scale = IconScale(entry.Nozzle);
            size *= scale;
            rect = new Rect(rect.center.x - size * 0.5f, rect.center.y - size * 0.5f, size, size);
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
