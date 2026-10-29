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
    // Melee plugin: its `ssbm.dll` and its cache (which it extracts PlMr.dat into on first run)
    // are found at runtime, and if either is absent the empty button stays a no-op.
    internal static class MarioMelee
    {
        const uint AttackButton = 2; // libssbm ATTACK
        // Melee unit -> world metres (the Melee plugin's MetresPerUnit default). Sunshine's
        // UnityHost.Scale is 0.01 for its 160-unit body; Melee's fighter is ~11 units, so the
        // hitbox positions and radii must use this scale instead.
        const float MeleeScale = 0.1f;
        // The lab sim is kept a little above the floor while RoR2 says Mario is airborne, so
        // the A selector picks an aerial (AttackAirN/F/B/Hi/Lw) instead of a grounded move.
        const float AirborneY = 25f;
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
        static byte sentMove;
        static float sentFrame;
        // The lab sim's own mixer voices the attacks: the subaction scripts' sound commands
        // (ftAction_80071B50 / ftAction_80071FC8) carry Mario's "hoo"/"wah" grunts and swing
        // sounds from main.ssm. The lab also walks and lands with RoR2, so it is heard only
        // while an attack plays and for this long after, letting a voice line finish.
        const float VoiceTail = 0.8f;
        static MeleeAudio audio;
        static float voiceUntil;

        public static bool Available
        {
            get
            {
                if (!tried) Load();
                return ready;
            }
        }

        /// Loads (or reports why it cannot) once, so the log says at startup whether the
        /// empty FLUDD button has Melee A attacks.
        public static void Probe() => _ = Available;

        /// The Melee plugin extracts PlMr.dat on its first run, after this plugin has loaded;
        /// a missing cache is looked for again this often instead of turning the attacks off.
        const float RetrySeconds = 5f;
        static float retryAt;

        static void Load()
        {
            if (Time.realtimeSinceStartup < retryAt) return;
            tried = true;
            try
            {
                string here = System.IO.Path.GetDirectoryName(typeof(MarioMelee).Assembly.Location);
                string cache = FindCache(here);
                if (cache == null)
                {
                    // Not latched: the Melee plugin may still be extracting from the disc.
                    tried = false;
                    retryAt = Time.realtimeSinceStartup + RetrySeconds;
                    if (!loggedMissing)
                    {
                        loggedMissing = true;
                        Plugin.Log.LogInfo("Melee Mario A attacks are waiting for the Melee plugin's cache (PlMr.dat); "
                            + "install the Melee plugin and set its Paths.Disc, or set Paths.MeleeAssets");
                    }
                    return;
                }
                // ssbm.dll belongs to the Melee plugin, not this one: load it from there.
                string dll = here == null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(here, "..", "Melee", "ssbm.dll"));
                if (string.IsNullOrEmpty(dll) || !File.Exists(dll))
                {
                    Plugin.Log.LogInfo("Melee Mario: the Melee plugin's ssbm.dll is missing; the empty FLUDD button stays empty");
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
                    MeleeNative.ssbm_mario_pose_free(pose);
                    pose = IntPtr.Zero;
                    return;
                }
                host = new MeleeNative.Host
                {
                    collide = Marshal.GetFunctionPointerForDelegate(collide),
                    evt = Marshal.GetFunctionPointerForDelegate(eventFn),
                };
                audio = new MeleeAudio(sim);
                MeleeAudio.SetGain(sim, 0f);
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

        static bool loggedMissing;

        /// The Melee cache holding PlMr.dat. Nothing is staged beside this plugin for it: an
        /// explicit Paths.MeleeAssets / $SSBM_ASSETS wins, then whatever folder the installed
        /// Melee plugin uses (its config, its assets_dir.txt, or its own assets/ folder).
        static string FindCache(string here)
        {
            bool Has(string dir) => !string.IsNullOrEmpty(dir) && File.Exists(System.IO.Path.Combine(dir, "PlMr.dat"));
            if (Has(MarioSkin.MeleeDir)) return MarioSkin.MeleeDir;
            string env = Environment.GetEnvironmentVariable("SSBM_ASSETS");
            if (Has(env)) return env.Trim();
            string loaded = MeleePluginAssets();
            if (Has(loaded)) return loaded;
            if (string.IsNullOrEmpty(here)) return null;
            string melee = System.IO.Path.GetFullPath(System.IO.Path.Combine(here, "..", "Melee"));
            string pointer = System.IO.Path.Combine(melee, "assets_dir.txt");
            if (File.Exists(pointer))
            {
                string target = File.ReadAllText(pointer).Trim();
                if (Has(target)) return target;
            }
            string own = System.IO.Path.Combine(melee, "assets");
            if (Has(own)) return own;
            string shared = GameCubeLibrary.AssetsFor(Plugin.LibraryRoot, GameCubeLibrary.Melee);
            return Has(shared) ? shared : null;
        }

        /// The running Melee plugin's resolved cache folder (its Plugin.AssetsDir), read by
        /// reflection so this plugin never compiles against it. Null when it is not loaded.
        static string MeleePluginAssets()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.libssbm.melee", out var info)
                    || info.Instance == null) return null;
                var field = info.Instance.GetType().GetField("AssetsDir",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                return field?.GetValue(null) as string;
            }
            catch { return null; }
        }

        /// The empty FLUDD button's tap. True when a Mario A attack started.
        public static bool TryAttack(CharacterBody body, Vector3 aim)
        {
            if (!body || !Available) return false;
            queued = true;
            return true;
        }

        /// Steps the Melee Mario. Call once per fixed step; [`Draw`] applies the pose.
        public static void Step(CharacterBody body, Vector3 aim, bool grounded)
        {
            if (!ready || sim == IntPtr.Zero || !body) return;
            var move = body.inputBank ? body.inputBank.moveVector : Vector3.zero;
            var pad = new MeleeNative.Pad
            {
                movement = new MeleeNative.Vec3(move.x, 0f, move.z),
                aim = new MeleeNative.Vec3(aim.x, aim.y, aim.z),
                // Only A goes to the lab. Sunshine's B is the dive, on the Sunshine sim.
                buttons = queued ? AttackButton : 0u,
                allowed = 31u,
            };
            queued = false;
            // Keep the lab sim's ground state in step with RoR2's; a punch in the air becomes
            // an aerial instead of a standing jab.
            MeleeNative.ssbm_sim_nudge(sim,
                new MeleeNative.Vec3(0f, grounded ? 0f : AirborneY, 0f), grounded ? 1u : 0u);
            MeleeNative.ssbm_sim_step(sim, in host, in pad, 1.0 / 60.0);
            Damage(body);
            if (MeleeNative.ssbm_sim_state(sim, out var state) == 0)
            {
                activeMove = 0;
                activeClip = null;
                return;
            }
            int moveId = (int)state.move_id;
            string clip = ClipFor(moveId);
            activeMove = clip == null ? 0 : moveId;
            if (clip != activeClip) hitOnce.Clear();
            activeClip = clip;
            activeFrame = clip == null ? 0f : MeleeNative.ssbm_sim_motion_frame(sim);
            if (activeMove != 0) voiceUntil = Time.time + VoiceTail;
            // Puppets draw the same clip without running a second Melee sim. Only a changed
            // pose is recorded, so idle frames stay the same size as before.
            if (activeMove != sentMove || activeFrame != sentFrame)
            {
                sentMove = (byte)activeMove;
                sentFrame = activeFrame;
                SimOps.Rec?.Melee(sentMove, sentFrame);
            }
        }

        static string ClipFor(int move)
        {
            return move switch
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
                var local = new Vector3(h.local_position.x, h.local_position.y, h.local_position.z);
                var facing = new Vector3(h.facing.x, 0f, h.facing.z);
                var rotation = facing.sqrMagnitude > 1e-6f
                    ? Quaternion.LookRotation(facing)
                    : Quaternion.identity;
                var at = body.footPosition + rotation * local * MeleeScale;
                // Melee percent -> RoR2 coefficient, through the same server-authoritative
                // hit path as stomps/water, so a client's punches land on the server and the
                // other clients see the same damage.
                var force = body.transform.forward * Mathf.Clamp(h.damage * 0.35f, 1f, 8f);
                MarioCombat.MeleeHit(body, at, h.radius * MeleeScale, h.damage / 3f, force);
            }
        }

        /// Gives the Sunshine view the active Melee clip's pose. Call before the view draws.
        public static void Draw(MarioView view)
        {
            ApplyPose(view, (byte)activeMove, activeFrame);
            if (audio == null || sim == IntPtr.Zero) return;
            MeleeAudio.SetGain(sim, activeMove != 0 || Time.time < voiceUntil ? 1f : 0f);
            audio.Update();
        }

        /// Applies a recorded Melee move/frame to any Mario view (the local player's, or a
        /// puppet replaying the owner's `Melee` op). `move` 0 clears the override.
        public static void ApplyPose(MarioView view, byte move, float frame)
        {
            if (view == null) return;
            if (move == 0 || !Available)
            {
                view.SetPose(null, 0);
                return;
            }
            string clip = ClipFor(move);
            if (clip == null)
            {
                view.SetPose(null, 0);
                return;
            }
            uint n = MeleeNative.ssbm_mario_pose_sample(
                pose, NativeLib.CString(clip), frame, matrices, (uint)MaxJoints);
            if (n == poseJoints) view.SetPose(matrices, n);
            else view.SetPose(null, 0);
        }

        public static void Reset()
        {
            queued = false;
            activeMove = 0;
            activeClip = null;
            sentMove = 0;
            sentFrame = 0f;
            hitOnce.Clear();
            voiceUntil = 0f;
            audio?.Dispose(); audio = null;
            if (sim != IntPtr.Zero) { MeleeAudio.SetGain(sim, 1f); MeleeNative.ssbm_sim_free(sim); sim = IntPtr.Zero; }
            if (pose != IntPtr.Zero) { MeleeNative.ssbm_mario_pose_free(pose); pose = IntPtr.Zero; }
            tried = false;
            ready = false;
            retryAt = 0f;
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

