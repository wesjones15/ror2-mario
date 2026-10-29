using System;
using System.IO;
using System.Runtime.InteropServices;
using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Mario's Melee contextual A attacks on the empty FLUDD button. The move is chosen by
    // libssbm's real Mario fighter (FighterKind::Mario, PlMr.dat/PlMrAJ.dat), so the jab, tilts,
    // smashes and aerials use their disc timing and hitboxes. The Sunshine body is only posed
    // with the Melee clip: `ssbm_mario_pose_sample` retargets the clip onto `ma_mdl1.bmd`, and
    // `sms_view_set_pose` draws that pose for the frame. Nothing here is compiled against the
    // Melee plugin: `ssbm.dll` and the Melee cache are found at runtime, and if either is absent
    // the empty button stays a no-op (the ability is omitted).
    internal static class MarioMelee
    {
        const uint AttackButton = 2; // libssbm ATTACK
        // Melee unit -> world metres (the Melee plugin's MetresPerUnit default). Sunshine's
        // UnityHost.Scale is 0.01 for its 160-unit body; Melee's fighter is ~11 units, so the
        // hitbox positions and radii must use this scale instead.
        const float MeleeScale = 0.1f;
        const int MaxJoints = 64;
        static bool tried, ready;
        static IntPtr sim, pose;
        static MeleeNative.Host host;
        static readonly MeleeNative.CollideFn collide = Floor;
        static readonly MeleeNative.EventFn eventFn = Event;
        static readonly float[] matrices = new float[MaxJoints * 16];
        static readonly MeleeNative.HitboxSample[] hitboxes = new MeleeNative.HitboxSample[24];
        static readonly System.Collections.Generic.HashSet<(ulong, uint, uint)> hitOnce =
            new System.Collections.Generic.HashSet<(ulong, uint, uint)>();
        static uint poseJoints;
        static bool queued;
        static int activeMove;
        static string activeClip;
        static float activeFrame;

        public static bool Available
        {
            get
            {
                if (!tried) Load();
                return ready;
            }
        }

        static void Load()
        {
            tried = true;
            try
            {
                string cache = MarioSkin.MeleeDir;
                if (string.IsNullOrEmpty(cache) || !File.Exists(System.IO.Path.Combine(cache, "PlMr.dat")))
                {
                    cache = Environment.GetEnvironmentVariable("SSBM_ASSETS");
                }
                if (string.IsNullOrEmpty(cache) || !File.Exists(System.IO.Path.Combine(cache, "PlMr.dat")))
                {
                    Plugin.Log.LogInfo("Melee Mario: no PlMr.dat in the Melee cache; the empty FLUDD button stays empty");
                    return;
                }
                string here = System.IO.Path.GetDirectoryName(typeof(MarioMelee).Assembly.Location);
                string dll = System.IO.Path.Combine(here ?? cache, "ssbm.dll");
                if (!File.Exists(dll))
                {
                    dll = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(cache) ?? cache, "ssbm.dll");
                }
                if (!File.Exists(dll))
                {
                    Plugin.Log.LogInfo($"Melee Mario: {dll} is missing; the empty FLUDD button stays empty");
                    return;
                }
                MeleeNative.Load(dll);
                string bmd = System.IO.Path.Combine(Plugin.AssetsDir, "bmd", "ma_mdl1.bmd");
                if (!File.Exists(bmd))
                {
                    Plugin.Log.LogInfo("Melee Mario: Sunshine's ma_mdl1.bmd is missing");
                    return;
                }
                pose = MeleeNative.ssbm_mario_pose_new(NativeLib.CString(cache), NativeLib.CString(bmd));
                if (pose == IntPtr.Zero)
                {
                    Plugin.Log.LogWarning("Melee Mario: pose load failed: " + MeleeNative.Error);
                    return;
                }
                sim = MeleeNative.ssbm_sim_new_fighter(NativeLib.CString(cache), default, MeleeNative.FighterMario);
                if (sim == IntPtr.Zero)
                {
                    Plugin.Log.LogWarning("Melee Mario: sim load failed: " + MeleeNative.Error);
                    return;
                }
                host = new MeleeNative.Host
                {
                    collide = Marshal.GetFunctionPointerForDelegate(collide),
                    evt = Marshal.GetFunctionPointerForDelegate(eventFn),
                };
                poseJoints = MeleeNative.ssbm_mario_pose_sample(pose, NativeLib.CString("Wait"), 0f, matrices, (uint)MaxJoints);
                ready = poseJoints > 0 && poseJoints <= MaxJoints;
                if (ready) Plugin.Log.LogInfo($"Melee Mario ready: {poseJoints} Sunshine joints, A attacks from {cache}");
                else Plugin.Log.LogWarning("Melee Mario: the ported pose has no joints");
            }
            catch (Exception e)
            {
                ready = false;
                Plugin.Log.LogWarning("Melee Mario unavailable: " + e.Message);
            }
        }

        /// The empty FLUDD button's tap. True when a Mario A attack started.
        public static bool TryAttack(CharacterBody body, Vector3 aim)
        {
            if (!body || !Available) return false;
            queued = true;
            return true;
        }

        /// Steps the Melee Mario. Call once per fixed step; [`Draw`] applies the pose.
        public static void Step(CharacterBody body, Vector3 aim)
        {
            if (!ready || sim == IntPtr.Zero || !body) return;
            var move = body.inputBank ? body.inputBank.moveVector : Vector3.zero;
            var pad = new MeleeNative.Pad
            {
                movement = new MeleeNative.Vec3(move.x, 0f, move.z),
                aim = new MeleeNative.Vec3(aim.x, aim.y, aim.z),
                buttons = queued ? AttackButton : 0u,
                allowed = 31u,
            };
            queued = false;
            MeleeNative.ssbm_sim_step(sim, in host, in pad, 1.0 / 60.0);
            Damage(body);
            if (MeleeNative.ssbm_sim_state(sim, out var state) == 0)
            {
                activeMove = 0;
                activeClip = null;
                return;
            }
            int moveId = (int)state.move_id;
            string clip = moveId switch
            {
                1 => "Attack11",
                2 => "AttackS3S",
                3 => "AttackHi3",
                4 => "AttackLw3",
                5 => "AttackDash",
                6 => "AttackAirN",
                7 => "AttackAirF",
                8 => "AttackAirHi",
                9 => "AttackAirLw",
                10 => "AttackS4S",
                11 => "AttackHi4",
                12 => "AttackLw4",
                13 => "Attack12",
                14 => "AttackAirB",
                15 => "Attack13",
                _ => null,
            };
            activeMove = clip == null ? 0 : moveId;
            if (clip != activeClip) hitOnce.Clear();
            activeClip = clip;
            activeFrame = clip == null ? 0f : MeleeNative.ssbm_sim_motion_frame(sim);
            if (clip == null) hitOnce.Clear();
        }

        /// Melee's own hitboxes, each generation once. The Melee plugin's damage
        /// coefficient is disc percent / 3, so the same here.
        static void Damage(CharacterBody body)
        {
            uint n = MeleeNative.ssbm_sim_hitboxes(sim, hitboxes, (uint)hitboxes.Length);
            for (int i = 0; i < n && i < hitboxes.Length; i++)
            {
                var h = hitboxes[i];
                if (!hitOnce.Add((h.serial, h.generation, h.id))) continue;
                if (h.damage <= 0f || h.radius <= 0f) continue;
                var at = body.footPosition + new Vector3(h.origin.x, h.origin.y, h.origin.z) * MeleeScale;
                new BlastAttack
                {
                    attacker = body.gameObject,
                    inflictor = body.gameObject,
                    teamIndex = body.teamComponent ? body.teamComponent.teamIndex : TeamIndex.Neutral,
                    baseDamage = h.damage / 3f,
                    damageType = DamageType.Generic,
                    procCoefficient = 1f,
                    position = at,
                    radius = h.radius * MeleeScale,
                    falloffModel = BlastAttack.FalloffModel.None,
                    baseForce = 0f,
                }.Fire();
            }
        }

        /// Gives the Sunshine view the active Melee clip's pose. Call before the view draws.
        public static void Draw(MarioView view)
        {
            if (view == null) return;
            if (activeClip == null || activeMove == 0)
            {
                view.SetPose(null, 0);
                return;
            }
            uint n = MeleeNative.ssbm_mario_pose_sample(
                pose, NativeLib.CString(activeClip), activeFrame, matrices, (uint)MaxJoints);
            if (n == poseJoints) view.SetPose(matrices, n);
            else view.SetPose(null, 0);
        }

        public static void Reset()
        {
            queued = false;
            activeMove = 0;
            activeClip = null;
            hitOnce.Clear();
            if (sim != IntPtr.Zero) { MeleeNative.ssbm_sim_free(sim); sim = IntPtr.Zero; }
            if (pose != IntPtr.Zero) { MeleeNative.ssbm_mario_pose_free(pose); pose = IntPtr.Zero; }
            tried = false;
            ready = false;
        }

        static void Floor(IntPtr user, in MeleeNative.Vec3 from, in MeleeNative.Vec3 to, out MeleeNative.Collision c)
        {
            c = new MeleeNative.Collision
            {
                position = new MeleeNative.Vec3(to.x, Mathf.Max(0f, to.y), to.z),
                grounded = to.y <= 0f ? 1u : 0u,
            };
        }

        static void Event(IntPtr user, in MeleeNative.Event e) { }
    }
}

