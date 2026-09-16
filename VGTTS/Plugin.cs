using System.Linq;
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using VGModAPI;
using VGTTS.Audio;
using VGTTS.Cache;
using VGTTS.Dialogue;
using VGTTS.Prerender;
using VGTTS.TTS;
using VGTTS.Voice;

namespace VGTTS;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
// Dialogue-line playback runs exclusively through the VGModAPI cooperative dialogue
// service (Subscribe + TryAcquirePresentation); 0.2.8 is the first prerelease that
// ships that surface, so the plugin will not load without it. Bar-roster observation
// rides the same service surface; ECHO/distress channels keep their own hooks.
[BepInDependency(ModApi.PluginId, "0.2.8")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "vgtts";
    public const string PluginName = "TTS";
    // BepInEx parses PluginVersion through System.Version which rejects SemVer
    // pre-release suffixes, so stick to the plain N.N.N form.
    public const string PluginVersion = "1.7.0";

    internal static Plugin Instance { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;

    internal ConfigEntry<bool> CfgEnabled = null!;
    internal ConfigEntry<bool> CfgDialogue = null!;
    internal ConfigEntry<bool> CfgEcho = null!;
    internal ConfigEntry<string> CfgCaptainPreset = null!;

    private Harmony _harmony = null!;
    private DialogueTtsCoordinator? _dialogueTts;

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

        Patches.BarRosterBridge.Start();
        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Patches.DialogueManagerPatches));
        _harmony.PatchAll(typeof(Patches.EchoRemarksPatches));
        _harmony.PatchAll(typeof(Patches.CharactersPatches));
        _harmony.PatchAll(typeof(Patches.DistressCombatPatches));
        _harmony.PatchAll(typeof(Patches.BarPatronPatches));
        _harmony.PatchAll(typeof(Patches.BarRefreshPatches));
        _harmony.PatchAll(typeof(Patches.SpaceStationInteriorPatches));
        TryBindDialogueTts();
        Log.LogInfo($"{PluginName} v{PluginVersion} loaded ({_harmony.GetPatchedMethods().Count()} patches)");
    }

    /// <summary>
    /// Canonical dialogue playback path: observe the API conversation manager and speak
    /// only lines whose presentation lease we win. There is deliberately no Harmony-hook
    /// fallback for presented lines — binding failure disables dialogue TTS instead.
    /// </summary>
    private void TryBindDialogueTts()
    {
        IDialogueService service;
        try
        {
            service = ModApi.Services.Dialogue;
        }
        catch (Exception ex)
        {
            Log.LogError($"[dialogue] VGModAPI dialogue service unreachable ({ex.GetType().Name}: {ex.Message}) — dialogue TTS disabled; ECHO/ambient channels still active.");
            return;
        }

        var controller = TtsController.Instance;
        if (controller == null) return;

        var coordinator = new DialogueTtsCoordinator(
            service, controller,
            DialogueSpeaker.ResolveFromName,
            () => CfgEnabled.Value && CfgDialogue.Value,
            msg => Log.LogInfo(msg), msg => Log.LogWarning(msg),
            ownerId: PluginGuid);

        if (!coordinator.TryAttach())
        {
            coordinator.Dispose();
            Log.LogError("[dialogue] could not subscribe to the VGModAPI dialogue service — dialogue TTS disabled; ECHO/ambient channels still active.");
            return;
        }

        _dialogueTts = coordinator;
        Log.LogInfo("[dialogue] dialogue TTS bound to VGModAPI dialogue service (cooperative presentation ownership).");
    }

    private void Update()
    {
        // Drains stops the lease token delivered off-thread (defensive; the API
        // cancels on the main thread). Cheap empty-queue check otherwise.
        _dialogueTts?.Pump();
    }

    private void OnDestroy()
    {
        _dialogueTts?.Dispose();
        Patches.BarRosterBridge.Current?.Dispose();
        _harmony?.UnpatchSelf();
    }
}
