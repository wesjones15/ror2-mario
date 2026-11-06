using System;
using System.Collections.Generic;
using System.Linq;
using EntityStates;
using RoR2;
using RoR2.Skills;
using Ror2Common;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // Bowser Jr. as a survivor (libsms koopajr.rs: TEnemyMario, Enemy/enemyMario.cpp, and
    // TLimitKoopaJr, Enemy/limitkoopajr.cpp). He is a second body on Mario's sim and driver:
    // Mario's moves and acrobatics, no FLUDD (sms_sim_set_koopajr takes it off and makes the goop
    // never catch him), the Shadow Mario skin (native skin 4: the kagemario body, its swirl and
    // the brush on his left hand) and, in his own form, Bowser Jr.'s model (look 9) posed by
    // sms_jr_state's clip. His buttons:
    //  * RT held (Shadow Mario, on the ground): the magic brush paints goop at his feet;
    //    RT tapped (Bowser Jr.): a goop ball;
    //  * RB tapped: change form; RB held 0.5 s: the next goop kind;
    //  * D-pad up (keyboard 1): Bowser Jr.'s sign (Shadow Mario) or a Piranha seed (Bowser Jr.);
    //  * D-pad down (keyboard 2): sink into the goop under him, or come back out.
    // The goop world (KoopaJrGoop) steps the goop, the Stus and the Piranhas and deals their
    // damage. Multiplayer: his machine runs his sim and sends his form, hiding and clip
    // (JrStateMsg) for the others to draw on his puppet; the server keeps hits off him while he
    // hides. His paint goes through the goop world's mirror (KoopaJrGoopNet).
    internal sealed class KoopaJrBody : MonoBehaviour
    {
        public const string BodyName = "KoopaJrTestBody";
        // TLimitKoopaJr's clips (koopajr_*.bck, by koopajr::jr_clip).
        static readonly string[] ClipNames = { "koopajr_damage", "koopajr_shoot", "koopajr_wait", "koopajr_yahoo" };
        static readonly bool[] ClipLoops = { false, false, true, true };
        const float KindHold = 0.5f;
        static readonly string[] KindNames =
            { "brown goop", "fire goop", "oil", "glass goop", "electric goop", "deep goop", "paint", "scorching goop" };

        CharacterBody body;
        IntPtr sim;
        Native.JrState state;
        bool haveState;
        // RB: a tap changes form, a hold picks the next kind (read each frame, used in the step).
        bool menuWas, menuSpent, formTap, kindTap;
        float menuSince;
        bool keyUp, keyDown;
        // His own form's model.
        IntPtr jrView;
        MarioFlowers.PropSource jrSource;
        ModelView jrModels;
        int[] clips;
        float[] frames;
        bool jrTried;
        byte lastClip = 255;
        float clipStart;

        public static bool Is(CharacterBody b) => b && b.GetComponent<KoopaJrBody>();

        /// His sim's id for him (his own goop shots pass through him): the same on every machine.
        public ulong Id => IdFor(body);

        public static ulong IdFor(CharacterBody b) =>
            0x2_0000_0000UL | (b && b.netId.Value != 0 ? b.netId.Value : (uint)(b ? b.gameObject.GetInstanceID() : 0));

        // What his machine last sent (or this machine was told), and when.
        Native.JrState sent;
        float sentAt = -10f, stateAt = -10f;
        const float StateKeepAlive = 1f, StateStale = 3f;

        /// Mario's model is not drawn: he is Bowser Jr. now, or under the goop.
        public bool HidesMario => haveState && (state.form == 1 || state.hidden != 0);
        public bool Hidden => haveState && state.hidden != 0;
        public bool JrForm => haveState && state.form == 1;

        void Awake() => body = GetComponent<CharacterBody>();

        void FixedUpdate()
        {
            // Another player's Bowser Jr. under the goop: nothing hits him (his sim is on his machine).
            if (NetworkServer.active && body && !body.hasEffectiveAuthority && sim == IntPtr.Zero
                && Hidden && Time.time - stateAt < StateStale)
                body.AddTimedBuff(RoR2Content.Buffs.HiddenInvincibility, 0.25f);
        }

        void OnDestroy() => FreeJr();

        /// The driver made his sim: Bowser Jr. on it, and the goop world if it is ready.
        public void Attach(IntPtr newSim)
        {
            sim = newSim;
            haveState = false;
            if (sim == IntPtr.Zero) return;
            Native.sms_sim_set_koopajr(sim, 1, Id);
            if (KoopaJrGoop.World != IntPtr.Zero) Native.sms_sim_set_goop(sim, KoopaJrGoop.World);
            Plugin.Log.LogInfo("Bowser Jr.'s sim: Shadow Mario's form, no FLUDD");
        }

        /// The player's RB this frame (the driver's Update, in place of FLUDD's nozzle wheel).
        public void ReadMenu(bool held)
        {
            if (held && !menuWas)
            {
                menuSince = Time.unscaledTime;
                menuSpent = false;
            }
            if (held && !menuSpent && Time.unscaledTime - menuSince >= KindHold)
            {
                menuSpent = true;
                kindTap = true;
            }
            if (!held && menuWas && !menuSpent) formTap = true;
            menuWas = held;
            // A keyboard has no d-pad: 1 and 2 stand in for up and down.
            keyUp |= Input.GetKeyDown(KeyCode.Alpha1);
            keyDown |= Input.GetKeyDown(KeyCode.Alpha2);
        }

        /// His buttons, inside the driver's step before the sim reads the pad. Consumes the
        /// d-pad and trigger taps Mario would have used.
        public void Buttons(ref Native.Host host, MarioPad pad, bool local)
        {
            bool up = pad.TakeUp() | keyUp, down = pad.TakeRefill() | keyDown, shot = pad.TakeTriggerTap();
            bool form = formTap, kind = kindTap;
            keyUp = keyDown = formTap = kindTap = false;
            if (!local || sim == IntPtr.Zero) return;
            Read();
            if (form) Act(ref host, 0);
            if (kind && Act(ref host, 7))
            {
                Read();
                int k = state.paint_kind;
                Plugin.Log.LogInfo($"Bowser Jr. paints {(k < KindNames.Length ? KindNames[k] : "goop " + k)}");
            }
            bool jr = state.form == 1;
            if (up) Act(ref host, jr ? 5u : 3u);
            if (down) Act(ref host, state.hidden != 0 ? 2u : 1u);
            if (shot && jr) Act(ref host, 4);
        }

        bool Act(ref Native.Host host, uint action) => Native.sms_jr_action(sim, ref host, action) != 0;

        void Read() => haveState = sim != IntPtr.Zero && Native.sms_jr_state(sim, out state) != 0;

        /// After the step: his state for drawing; under the goop nothing hits him.
        public void AfterStep()
        {
            Read();
            if (Hidden && NetworkServer.active && body)
                body.AddTimedBuff(RoR2Content.Buffs.HiddenInvincibility, 0.25f);
            if (haveState && Mp.Networked) SendState();
        }

        // ---- multiplayer ----

        public static void RegisterNet() => Mp.Register(MarioNet.JrStateMsg, OnServer, OnClient);

        /// His form, hiding, clip and paint when they change (and now and then), for the others.
        void SendState()
        {
            bool changed = state.form != sent.form || state.hidden != sent.hidden || state.jr_clip != sent.jr_clip
                || state.paint_kind != sent.paint_kind;
            if (!changed && Time.time - sentAt < StateKeepAlive) return;
            if (!body || body.netId.Value == 0) return;
            sent = state;
            sentAt = Time.time;
            var w = new ByteWriter(12);
            w.U8(1);
            w.U32(body.netId.Value);
            w.U8(state.form);
            w.U8(state.hidden);
            w.U8(state.jr_clip);
            w.U8(state.paint_kind);
            Mp.SendToServer(MarioNet.JrStateMsg, w.ToArray());
        }

        static KoopaJrBody Apply(ByteReader r)
        {
            if (r.U8() != 1) return null;
            var go = Mp.Find(r.U32());
            byte form = r.U8(), hidden = r.U8(), clip = r.U8(), kind = r.U8();
            if (r.Failed || !go) return null;
            var jr = go.GetComponent<KoopaJrBody>();
            // His own machine reads his sim.
            if (!jr || !jr.body || jr.body.hasEffectiveAuthority) return null;
            jr.state = new Native.JrState { form = (byte)(form != 0 ? 1 : 0), hidden = (byte)(hidden != 0 ? 1 : 0), jr_clip = clip, paint_kind = kind };
            jr.haveState = true;
            jr.stateAt = Time.time;
            return jr;
        }

        static void OnServer(NetworkConnection from, ByteReader r)
        {
            // The host's own comes in with `from` null: its client side gets it below.
            if (from != null && Apply(r) == null) return;
            Mp.SendToAllExcept(MarioNet.JrStateMsg, r.Raw, from);
        }

        static void OnClient(NetworkConnection from, ByteReader r)
        {
            if (!NetworkServer.active) Apply(r);
        }

        /// Bowser Jr.'s own model at his feet, facing as the sim's body does: his own sim, or on
        /// another player's machine the puppet's shadow sim (`puppetSim`).
        public void Draw(Vector3 feet, IntPtr puppetSim = default)
        {
            var from = sim != IntPtr.Zero ? sim : puppetSim;
            bool fresh = sim != IntPtr.Zero || Time.time - stateAt < StateStale;
            if (!JrForm || Hidden || !fresh || from == IntPtr.Zero || !EnsureJr())
            {
                jrModels?.HideAll();
                return;
            }
            Native.sms_sim_state(from, out var st);
            int clip = Mathf.Clamp(state.jr_clip, 0, ClipNames.Length - 1);
            if (clip != lastClip)
            {
                lastClip = (byte)clip;
                clipStart = Time.time;
            }
            // The disc's clips run at 60 frames a second (SMSGetAnmFrameRate).
            float frame = (Time.time - clipStart) * 60f;
            float length = frames[clip];
            if (length > 1f) frame = ClipLoops[clip] ? frame % length : Mathf.Min(frame, length - 1f);
            Native.sms_view_prop_update(jrView, st.model_yaw, clips[clip], frame, 1);
            var rot = Quaternion.Euler(0f, st.model_yaw * (180f / 32768f), 0f);
            var at = feet / UnityHost.Scale;
            jrSource.draw = new ViewDraw
            {
                key = 1, model = 0, pos = new ViewVec3 { x = at.x, y = at.y, z = at.z },
                rot_x = rot.x, rot_y = rot.y, rot_z = rot.z, rot_w = rot.w, alpha = 1f,
            };
            jrModels.Update(UnityHost.Scale);
        }

        bool EnsureJr()
        {
            if (jrModels != null) return true;
            if (jrTried || !Plugin.AssetsReady || !Plugin.DrawModel.Value) return false;
            jrTried = true;
            jrView = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, Native.LookKoopaJr);
            if (jrView == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Bowser Jr.'s model isn't in the assets folder (enemy/koopajr); his form draws nothing");
                return false;
            }
            clips = new int[ClipNames.Length];
            frames = new float[ClipNames.Length];
            for (int i = 0; i < ClipNames.Length; i++)
                clips[i] = Native.sms_view_prop_clip(jrView, NativeLib.CString(ClipNames[i]), out frames[i]);
            jrSource = new MarioFlowers.PropSource(jrView);
            jrModels = ModelView.Create(jrSource, "Bowser Jr.");
            if (jrModels == null) FreeJr();
            return jrModels != null;
        }

        void FreeJr()
        {
            jrModels?.Dispose();
            jrModels = null;
            if (jrView != IntPtr.Zero) Native.sms_view_free(jrView);
            jrView = IntPtr.Zero;
        }

        // ---- the survivor ----

        public static NativeSkillDef Brush, Shot;

        /// A second survivor beside Mario: Commando's body with Mario's driver, made Bowser Jr.
        public static void Register(RoR2.ContentManagement.ContentPack pack, GameObject commando, GameObject commandoDisplay)
        {
            var body = Prefabs.Clone(commando, BodyName, Plugin.Guid);
            foreach (var ragdoll in body.GetComponentsInChildren<RagdollController>(true))
                UnityEngine.Object.DestroyImmediate(ragdoll);
            var cb = body.GetComponent<CharacterBody>();
            cb.baseNameToken = "KOOPAJRTEST_BODY_NAME";
            cb.subtitleNameToken = "KOOPAJRTEST_BODY_SUBTITLE";
            cb.bodyColor = new Color(0.16f, 0.2f, 0.62f);
            cb.baseMaxHealth = Plugin.MaxHealth.Value;
            cb.levelMaxHealth = Plugin.HealthPerLevel.Value;
            foreach (var esm in body.GetComponents<EntityStateMachine>())
                if (esm.customName == "Body")
                    esm.mainStateType = new SerializableEntityStateType(typeof(MarioMain));
            body.AddComponent<NativeBody>();
            body.AddComponent<KoopaJrBody>();
            body.AddComponent<MarioDriver>();
            body.AddComponent<MarioPuppet>();
            AddSkills(body, pack);
            MarioSkin.OneSkin(body, "KoopaJrSkinDefault", "KOOPAJRTEST_SKIN_DEFAULT", cb.bodyColor);

            var display = Prefabs.Clone(commandoDisplay, "KoopaJrTestDisplay", Plugin.Guid);
            if (Plugin.DrawModel.Value)
            {
                DisplayModel.Attach(display, Plugin.Guid + ".koopajr", () => MarioDisplay.Get(MarioSkin.Shadow));
                // libsms-present view::models: FLUDD's wg_mdl1.bmd (WaterGun.cpp, TWaterGun::init)
                // followed by its nozzle draws. The portrait should frame Shadow Mario and his brush.
                const uint fluddPack = 6, lastFluddNozzle = 11;
                Portrait.Request(body, () => MarioDisplay.Get(MarioSkin.Shadow),
                    hide: draw => draw.model >= fluddPack && draw.model <= lastFluddNozzle,
                    framing: PortraitFraming.ThreeQuarter, assetsDir: () => Plugin.AssetsDir,
                    revision: "shadow-character-v2");
            }

            var survivor = ScriptableObject.CreateInstance<SurvivorDef>();
            survivor.cachedName = "KoopaJrTest";
            survivor.bodyPrefab = body;
            survivor.displayPrefab = display;
            survivor.displayNameToken = "KOOPAJRTEST_BODY_NAME";
            survivor.descriptionToken = "KOOPAJRTEST_BODY_DESCRIPTION";
            survivor.outroFlavorToken = "KOOPAJRTEST_BODY_OUTRO";
            survivor.mainEndingEscapeFailureFlavorToken = "KOOPAJRTEST_BODY_FAIL";
            survivor.primaryColor = cb.bodyColor;
            survivor.desiredSortPosition = 101.5f; // After Mario and Olimar in either lobby mode, before Melee.
            pack.bodyPrefabs.Add(new[] { body });
            // Hidden for this release: his body stays registered (the goop world and net messages need it),
            // but he is not a survivor, so he does not show in character select.
            Plugin.Log.LogInfo("Bowser Jr. registered (hidden from character select)");
        }

        static void AddSkills(GameObject body, RoR2.ContentManagement.ContentPack pack)
        {
            var loc = body.GetComponent<SkillLocator>();
            Sprite Icon(SkillSlot s) => loc && loc.GetSkill(s) && loc.GetSkill(s).skillFamily
                ? loc.GetSkill(s).skillFamily.defaultSkillDef?.icon : null;
            Brush = NativeSkills.Make("KoopaJrBrush", "KOOPAJRTEST_BRUSH_NAME", "KOOPAJRTEST_BRUSH_DESC",
                Icon(SkillSlot.Primary), 0f, 1, true);
            Shot = NativeSkills.Make("KoopaJrForm", "KOOPAJRTEST_FORM_NAME", "KOOPAJRTEST_FORM_DESC",
                Icon(SkillSlot.Secondary), 0f, 1, true);
            NativeSkills.SetFamily(body, SkillSlot.Primary, Brush, pack);
            NativeSkills.SetFamily(body, SkillSlot.Secondary, Shot, pack);
            // Mario's own pound and slide: MarioItems spends and gates them by these very defs.
            // They are already in the pack (Mario's families), so only the new families go in.
            var scratch = new RoR2.ContentManagement.ContentPack();
            NativeSkills.SetFamily(body, SkillSlot.Utility, MarioSkills.Pound, scratch);
            NativeSkills.SetFamily(body, SkillSlot.Special, MarioSkills.Slide, scratch);
            pack.skillFamilies.Add(scratch.skillFamilies.ToArray());
        }

        public static void Tokens(Language lang)
        {
            lang.SetStringByToken("KOOPAJRTEST_BODY_NAME", "Bowser Jr.");
            lang.SetStringByToken("KOOPAJRTEST_BODY_SUBTITLE", "Shadow Mario");
            lang.SetStringByToken("KOOPAJRTEST_SKIN_DEFAULT", "Shadow Mario");
            lang.SetStringByToken("KOOPAJRTEST_BODY_DESCRIPTION",
                "Bowser Jr. stole the magic brush and Mario's good name. He has all of Mario's moves but no FLUDD, " +
                "and he paints Isle Delfino's goop wherever he goes.\n\n" +
                "< ! > Hold RT as Shadow Mario to paint goop: enemies in it are slowed, burnt, poisoned or swallowed.\n\n" +
                "< ! > Swoopin' Stus grow out of your goop and leap at enemies; spit Piranha seeds as Bowser Jr.\n\n" +
                "< ! > Tap RB to change between Shadow Mario and Bowser Jr.; hold it to choose the next goop.\n\n" +
                "< ! > Sink into your own goop with D-pad down to hide from every hit.");
            lang.SetStringByToken("KOOPAJRTEST_BRUSH_NAME", "Magic Brush");
            lang.SetStringByToken("KOOPAJRTEST_BRUSH_DESC",
                "<style=cIsUtility>Hold RT</style> on the ground to paint goop at your feet. Landings splat it too. " +
                "<style=cIsUtility>D-pad up</style> paints Bowser Jr.'s sign. As Bowser Jr., <style=cIsUtility>tap RT</style> to spit a goop ball.");
            lang.SetStringByToken("KOOPAJRTEST_FORM_NAME", "Change Form");
            lang.SetStringByToken("KOOPAJRTEST_FORM_DESC",
                "<style=cIsUtility>Tap RB</style> to change between Shadow Mario and Bowser Jr. " +
                "<style=cIsUtility>Hold RB</style> to choose the next kind of goop. As Bowser Jr., " +
                "<style=cIsUtility>D-pad up</style> spits a Piranha seed.");
            lang.SetStringByToken("KOOPAJRTEST_BODY_OUTRO", "..and so he left, to tell Papa everything.");
            lang.SetStringByToken("KOOPAJRTEST_BODY_FAIL", "..and so he vanished into his own goop.");
        }

        public static ControlSheet Sheet(CharacterBody body)
        {
            var sheet = new ControlSheet { Title = "Bowser Jr.", Subtitle = "Shadow Mario... On Vacation!" };
            sheet.Pad("")
                .Add("A", "Jump")
                .Add("B", "Dive")
                .Add("LS", "Move (click: Sprint)")
                .Add("LT in air", "Ground Pound");
            sheet.Pad("Brush")
                .Add("hold RT", "Paint goop (Shadow Mario)")
                .Add("tap RT", "Goop ball (Bowser Jr.)")
                .Add("tap RB", "Change form")
                .Add("hold RB 0.5s", "Next goop kind")
                .Add("D-pad up", "Sign / Piranha seed")
                .Add("D-pad down", "Sink into goop / come out");
            sheet.Keys("")
                .Add("Jump", "Jump")
                .Add("Secondary", "Dive")
                .Add("Utility in air", "Ground Pound");
            sheet.Keys("Brush")
                .Add("hold Primary", "Paint goop (Shadow Mario)")
                .Add("tap Primary", "Goop ball (Bowser Jr.)")
                .Add("tap Special", "Change form")
                .Add("hold Special 0.5s", "Next goop kind")
                .Add("1", "Sign / Piranha seed")
                .Add("2", "Sink into goop / come out");
            ControlSheetLayout.SpaceRows(sheet);
            return sheet;
        }
    }
}
