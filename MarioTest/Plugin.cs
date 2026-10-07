using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EntityStates;
using RoR2;
using RoR2.ContentManagement;
using UnityEngine;
using UnityEngine.Networking;
using Path = System.IO.Path;

namespace MarioTest
{
    // A throwaway survivor to try libsms's Mario movement in Risk of Rain 2: Commando's body,
    // model and skills, with the body moved by the sim instead of RoR2's character controller.
    // No R2API: content goes in through RoR2's own content-pack hook.
    [BepInPlugin(Guid, "Mario (test)", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.libsms.mariotest";

        internal static ManualLogSource Log;
        internal static string AssetsDir;
        internal static ConfigEntry<bool> LogStatus;

        void Awake()
        {
            Log = Logger;
            string here = Path.GetDirectoryName(Info.Location);

            var assets = Config.Bind("Paths", "AssetsDir", "",
                "libsms's assets/ folder (extracted from your own Super Mario Sunshine disc). " +
                "Empty: read assets_dir.txt next to the plugin, then $SMS_ASSETS.");
            UnityHost.Scale = Config.Bind("Sim", "MetresPerUnit", 0.01f,
                "Metres per Sunshine unit. 0.01 makes Mario 1.6 m tall.").Value;
            LogStatus = Config.Bind("Debug", "LogStatus", true,
                "Log each change of Mario's status (Wait, Running, Jump...) to the BepInEx log.");
            AssetsDir = ResolveAssets(assets.Value, here);

            if (!Native.Load(Path.Combine(here, "sms.dll"), out string error))
            {
                Log.LogError(error);
                return;
            }
            if (AssetsDir == null || !Directory.Exists(Path.Combine(AssetsDir, "params_mario")))
            {
                Log.LogError($"No libsms assets at '{AssetsDir}' (need params_mario/). " +
                             "Set Paths.AssetsDir in BepInEx/config/" + Guid + ".cfg.");
                return;
            }
            Log.LogInfo($"libsms ABI {Native.sms_abi_version()}, assets {AssetsDir}");

            ContentManager.collectContentPackProviders += add => add(new Content());
            Language.onCurrentLanguageChanged += AddTokens;
        }

        static string ResolveAssets(string configured, string here)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            string file = Path.Combine(here, "assets_dir.txt");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
            return Environment.GetEnvironmentVariable("SMS_ASSETS");
        }

        static void AddTokens()
        {
            var lang = Language.currentLanguage;
            if (lang == null) return;
            lang.SetStringByToken("MARIOTEST_BODY_NAME", "Mario (test)");
            lang.SetStringByToken("MARIOTEST_BODY_SUBTITLE", "Super Mario Sunshine movement");
            lang.SetStringByToken("MARIOTEST_BODY_DESCRIPTION",
                "Commando's body moved by libsms's Mario.\n\n" +
                "< ! > Jump: A. Primary: spray. Secondary: B (dive, punch).\n" +
                "< ! > Utility: L (ground pound, crouch). Sprint: plant and spray. Special: switch nozzle.");
            lang.SetStringByToken("MARIOTEST_BODY_OUTRO", "..and so he left, still wet.");
            lang.SetStringByToken("MARIOTEST_BODY_FAIL", "..and so he vanished, mid-triple-jump.");
        }
    }

    internal class Content : IContentPackProvider
    {
        readonly ContentPack pack = new ContentPack();

        public string identifier => Plugin.Guid;

        public IEnumerator LoadStaticContentAsync(LoadStaticContentAsyncArgs args)
        {
            var commando = LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterBodies/CommandoBody");
            var commandoDisplay = LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterDisplays/CommandoDisplay");
            if (!commando || !commandoDisplay)
            {
                Plugin.Log.LogError($"Commando not found (body {(bool)commando}, display {(bool)commandoDisplay})");
                yield break;
            }

            var body = Prefabs.Clone(commando, "MarioTestBody", networked: true);
            var cb = body.GetComponent<CharacterBody>();
            cb.baseNameToken = "MARIOTEST_BODY_NAME";
            cb.subtitleNameToken = "MARIOTEST_BODY_SUBTITLE";
            cb.bodyColor = new Color(0.9f, 0.1f, 0.1f);
            foreach (var esm in body.GetComponents<EntityStateMachine>())
                if (esm.customName == "Body")
                    esm.mainStateType = new SerializableEntityStateType(typeof(MarioMain));
            body.AddComponent<MarioDriver>();

            var display = Prefabs.Clone(commandoDisplay, "MarioTestDisplay", networked: false);

            var survivor = ScriptableObject.CreateInstance<SurvivorDef>();
            survivor.cachedName = "MarioTest";
            survivor.bodyPrefab = body;
            survivor.displayPrefab = display;
            survivor.displayNameToken = "MARIOTEST_BODY_NAME";
            survivor.descriptionToken = "MARIOTEST_BODY_DESCRIPTION";
            survivor.outroFlavorToken = "MARIOTEST_BODY_OUTRO";
            survivor.mainEndingEscapeFailureFlavorToken = "MARIOTEST_BODY_FAIL";
            survivor.primaryColor = cb.bodyColor;
            survivor.desiredSortPosition = 100f;

            pack.bodyPrefabs.Add(new[] { body });
            pack.survivorDefs.Add(new[] { survivor });
            pack.entityStateTypes.Add(new[] { typeof(MarioMain) });
            Plugin.Log.LogInfo("Mario (test) registered");
        }

        public IEnumerator GenerateContentPackAsync(GetContentPackAsyncArgs args)
        {
            ContentPack.Copy(pack, args.output);
            yield break;
        }

        public IEnumerator FinalizeAsync(FinalizeAsyncArgs args)
        {
            yield break;
        }
    }

    internal static class Prefabs
    {
        static GameObject holder;

        /// A prefab copy, kept under an inactive object so it never wakes up in the scene. A
        /// networked copy gets its own asset id, so clients spawn it rather than Commando
        /// (what R2API's PrefabAPI.InstantiateClone does).
        public static GameObject Clone(GameObject src, string name, bool networked)
        {
            if (!holder)
            {
                holder = new GameObject("MarioTestPrefabs");
                holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(holder);
            }
            var copy = UnityEngine.Object.Instantiate(src, holder.transform);
            copy.name = name;
            var id = copy.GetComponent<NetworkIdentity>();
            if (id)
            {
                if (!networked)
                    Plugin.Log.LogWarning($"{name} has a NetworkIdentity but is not registered as networked");
                var hash = NetworkHash128.Parse(Md5Hex(Plugin.Guid + "/" + name));
                typeof(NetworkIdentity)
                    .GetField("m_AssetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(id, hash);
            }
            return copy;
        }

        static string Md5Hex(string s)
        {
            using (var md5 = MD5.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in md5.ComputeHash(Encoding.UTF8.GetBytes(s))) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    // The body's main state: RoR2's animation bookkeeping (BaseCharacterMain) without
    // GenericCharacterMain's movement, jumping and skill use, which belong to the sim now.
    public class MarioMain : BaseCharacterMain
    {
    }
}
