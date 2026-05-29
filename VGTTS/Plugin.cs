using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using VGTTS.Audio;
using VGTTS.Cache;
using VGTTS.Prerender;
using VGTTS.TTS;
using VGTTS.Voice;

namespace VGTTS;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "vgtts";
    public const string PluginName = "Vanguard Galaxy TTS";
    // BepInEx parses PluginVersion through System.Version which rejects SemVer
    // pre-release suffixes, so stick to the plain N.N.N form.
    public const string PluginVersion = "1.6.2";

    internal static Plugin Instance { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;

    internal ConfigEntry<bool> CfgEnabled = null!;
    internal ConfigEntry<bool> CfgDialogue = null!;
    internal ConfigEntry<bool> CfgEcho = null!;
    internal ConfigEntry<string> CfgCaptainPreset = null!;

    private Harmony _harmony = null!;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        CfgEnabled = Config.Bind("General", "Enabled", true, "Master enable/disable for TTS.");
        CfgDialogue = Config.Bind("General", "DialogueTTS", true, "Speak NPC dialogue lines.");
        CfgEcho = Config.Bind("General", "EchoTTS", true, "Speak ECHO ambient HUD tips.");
        CfgCaptainPreset = Config.Bind("Voice", "CaptainPreset", "auto",
            "Voice preset for the player captain. 'auto' picks m1 or f1 based on " +
            "commander gender. Options: auto, m1 (am_fenrir rugged American), " +
            "m2 (am_onyx deep), m3 (bm_fable British rogue), f1 (af_alloy calm), " +
            "f2 (bf_alice British), f3 (af_heart warm flagship).");

        KokoroProvider provider;
        try
        {
            provider = new KokoroProvider();
        }
        catch (System.IO.FileNotFoundException ex)
        {
            Log.LogError($"Kokoro bundle missing, TTS disabled: {ex.Message}");
            return;
        }

        var voiceMapper = new VoiceMapper(Config, defaultVoice: "kokoro:0", DefaultVoiceMap.Seeds);
        var prerender = new PrerenderLookup();
        var unprerendered = new UnprerenderedLog();
        TtsController.Instance = new TtsController(provider, new DiskCache(), voiceMapper, prerender, unprerendered);
        Log.LogInfo($"Kokoro TTS loaded. NPC profiles seeded: {DefaultVoiceMap.Seeds.Count}, " +
                    $"prerender entries: {prerender.EntryCount}, " +
                    $"prior prerender-misses: {unprerendered.SeenCount}");

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Patches.DialogueManagerPatches));
        _harmony.PatchAll(typeof(Patches.EchoRemarksPatches));
        _harmony.PatchAll(typeof(Patches.CharactersPatches));
        _harmony.PatchAll(typeof(Patches.DistressCombatPatches));
        _harmony.PatchAll(typeof(Patches.BarPatronPatches));
        _harmony.PatchAll(typeof(Patches.BarRefreshPatches));
        _harmony.PatchAll(typeof(Patches.SpaceStationInteriorPatches));
        Log.LogInfo($"{PluginName} v{PluginVersion} loaded ({_harmony.GetPatchedMethods().Count()} patches)");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}
