using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Source.Dialogues;
using Source.Galaxy.POI;
using Source.Galaxy.POI.Station;
using Source.Galaxy.POI.Station.Patrons;
using VGTTS.Audio;
using VGTTS.Text;

namespace VGTTS.Patches;

/// <summary>
/// Pre-warms the TTS cache for space-station bar patrons — salesmen + crew
/// recruiters — as soon as they're initialized, well before the player walks
/// in to interact.
///
/// Both patron types are procedurally named:
///   Salesman:   random firstName + 1 of 4 dialogue variants, fully resolved
///               at Salesman.InitializeData() time (stored in private
///               dialogueLines field). Caller: Bar.CreateBarPatron().
///   CrewMember: random CrewMemberData, fixed 1-line dialogue built on the
///               fly in InteractWithPatron() using crewMember.firstName.
///
/// Bar.CreateBarPatron creates N patrons per station, each gets
/// <c>BarPatron.Initialize()</c> called right after construction. That's our
/// hook: fires once, at which point both Salesman's dialogueLines and
/// CrewMember's crewMember are fully populated.
/// </summary>
[HarmonyPatch(typeof(BarPatron))]
internal static class BarPatronPatches
{
    private const string CrewRecruitLine = "Heya, heard you're looking for crew?";

    // Procedural-NPC defaults — chosen to be Kokoro SIDs no named NPC uses,
    // so random bar patrons don't accidentally sound like Greg or Elena.
    private const string ProceduralMaleVoice   = "kokoro:12";  // am_echo
    private const string ProceduralFemaleVoice = "kokoro:9";   // af_sarah

    /// <summary>Per-patron record of the (speaker, text) pairs we warmed.
    /// ConditionalWeakTable so the entries GC when the BarPatron does.</summary>
    private static readonly ConditionalWeakTable<BarPatron, List<(string Speaker, string Text)>> _patronLines = new();

    /// <summary>
    /// Tasks spawned by Initialize_Postfix are appended here so callers
    /// that trigger a batch of Initialize() calls (e.g.
    /// <see cref="SpaceStationInteriorPatches"/>) can await completion of the
    /// whole batch and log timing. Reset by <see cref="DrainPendingWarms"/>.
    /// </summary>
    private static readonly List<Task> _pendingWarms = new();
    private static readonly object _pendingLock = new();

    /// <summary>Serializes the actual Kokoro synth work so save-load doesn't
    /// queue 50 concurrent synthesis jobs. One patron at a time, highest
    /// priority first (see <see cref="LowPriorityStagger"/>).</summary>
    private static readonly SemaphoreSlim _warmGate = new(1, 1);

    /// <summary>How long low-priority warm tasks wait before grabbing the
    /// serial gate. Gives high-priority (currently-docked station) tasks
    /// time to reach the gate first so they get synthesized before the
    /// rest of the galaxy's patrons line up. 250ms is well under the time
    /// it takes the player to walk from dock to bar.</summary>
    private static readonly TimeSpan LowPriorityStagger = TimeSpan.FromMilliseconds(250);

    /// <summary>Atomically take a snapshot of pending warm tasks and clear
    /// the backlog. Returns the tasks the caller should <c>WhenAll</c> on.</summary>
    internal static List<Task> DrainPendingWarms()
    {
        lock (_pendingLock)
        {
            var snapshot = new List<Task>(_pendingWarms);
            _pendingWarms.Clear();
            return snapshot;
        }
    }

    /// <summary>Internal accessor for BarRefreshPatches — hands over the warmed
    /// lines for a patron about to go out of scope so they can be evicted.</summary>
    internal static bool TryTakeLines(BarPatron patron, out List<(string Speaker, string Text)> lines)
    {
        if (_patronLines.TryGetValue(patron, out lines))
        {
            _patronLines.Remove(patron);
            return true;
        }
        lines = null!;
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(BarPatron.Initialize))]
    private static void Initialize_Postfix(BarPatron __instance)
    {
        // Initialize() fires on every BarUI open (and every dialogue close
        // that triggers a UI refresh), for the SAME patrons. Skip if we've
        // already warmed this instance — cache is already hot, no need to
        // re-register voices or re-queue tasks.
        if (__instance != null && _patronLines.TryGetValue(__instance, out _)) return;
        if (__instance == null) return;

        // Priority: save-load fires Initialize on every visited station's
        // patrons in a single burst. We want the currently-docked station's
        // patrons to synth first so the player's nearest bar is ready before
        // they walk in. Everyone else still gets warmed — just queued behind.
        var currentBar = SpaceStation.current?.bar;
        var isHighPriority = currentBar != null && currentBar.availablePatrons.Contains(__instance);

        var type = __instance.GetType().Name;
        var hasCrew = (__instance as CrewMember)?.crewMember != null;
        Plugin.Log.LogDebug($"[bar-hook] {type} Initialize fired (name={__instance.name} hasCrewData={hasCrew} prio={(isHighPriority ? "high" : "low")})");

        var controller = TtsController.Instance;
        if (controller == null) return;
        if (Plugin.Instance == null || !Plugin.Instance.CfgEnabled.Value || !Plugin.Instance.CfgDialogue.Value) return;

        // Gender-aware voice for the patron's own lines (their dialogue + any
        // counterpart character the Salesman constructs). BarPatron exposes
        // isMale polymorphically — Salesman reads its own _isMale, CrewMember
        // checks crewMember.gender.
        var patronVoice = __instance.isMale ? ProceduralMaleVoice : ProceduralFemaleVoice;

        var pairs = new List<(string Speaker, string Text)>();
        switch (__instance)
        {
            case Salesman salesman:
                // Private field: dialogueLines. Traverse the fully-resolved list —
                // covers all 4 SalesmanXxx variants without caring which rolled.
                var lines = Traverse.Create(salesman).Field<List<DialogueLine>>("dialogueLines").Value;
                if (lines == null) return;
                var salesmanName = salesman.name;
                controller.RegisterVoice(salesmanName, patronVoice);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line?.text)) continue;
                    var speaker = line.character == Characters.captain
                        ? ResolveCaptainSpeaker()
                        : (line.character?.name ?? salesmanName);
                    pairs.Add((speaker, line.text));
                }
                break;

            case CrewMember crew when crew.crewMember != null:
                // Dialogue is hardcoded in CrewMember.InteractWithPatron; we know
                // it upfront so pre-warm it with the current crewMember's firstName.
                controller.RegisterVoice(crew.crewMember.firstName, patronVoice);
                pairs.Add((crew.crewMember.firstName, CrewRecruitLine));
                break;

            default:
                return;
        }

        if (pairs.Count == 0) return;

        _patronLines.AddOrUpdate(__instance, pairs);
        Plugin.Log.LogDebug($"[bar-warm] {__instance.GetType().Name} '{__instance.name}' — warming {pairs.Count} line(s)");

        var task = Task.Run(async () =>
        {
            // Low-priority tasks briefly wait so the currently-docked
            // station's patrons reach the serial gate first.
            if (!isHighPriority) await Task.Delay(LowPriorityStagger).ConfigureAwait(false);

            await _warmGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var (speaker, text) in pairs)
                {
                    try
                    {
                        await controller.WarmCacheAsync(speaker, TextNormalizer.ForTts(text), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch { /* best-effort; StartDialogue fallback warms again on click */ }
                }
            }
            finally { _warmGate.Release(); }
        });
        lock (_pendingLock) _pendingWarms.Add(task);
    }

    /// <summary>Mirror of DialogueManagerPatches.ResolveCaptainPreset — but we
    /// can't call private static from here, so resolve locally.</summary>
    private static string ResolveCaptainSpeaker()
    {
        var cfg = Plugin.Instance.CfgCaptainPreset.Value?.ToLowerInvariant() ?? "auto";
        if (cfg is "m1" or "m2" or "m3" or "f1" or "f2" or "f3") return "captain_" + cfg;
        var isFemale = Source.Player.GamePlayer.current?.commander?.gender == Source.Personnel.Gender.Female;
        return isFemale ? "captain_f1" : "captain_m1";
    }
}

/// <summary>
/// Evicts procedural-patron WAVs from the session cache when a bar refreshes
/// its roster (the game does this once per in-game day, wiping the old 5
/// patrons and rolling 5 new ones). Without this, the session cache carries
/// every patron the player has ever seen in any bar today.
///
/// Hooks <c>Bar.CheckUpdatePatrons</c> as prefix+postfix. The prefix snapshots
/// the current availablePatrons; the postfix diffs against the new list and
/// drops cache entries for any patron that left.
/// </summary>
[HarmonyPatch(typeof(Bar))]
internal static class BarRefreshPatches
{
    private static readonly ConditionalWeakTable<Bar, List<BarPatron>> _snapshots = new();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Bar.CheckUpdatePatrons))]
    private static void CheckUpdatePatrons_Prefix(Bar __instance)
    {
        _snapshots.AddOrUpdate(__instance, new List<BarPatron>(__instance.availablePatrons));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Bar.CheckUpdatePatrons))]
    private static void CheckUpdatePatrons_Postfix(Bar __instance)
    {
        if (!_snapshots.TryGetValue(__instance, out var before)) return;
        _snapshots.Remove(__instance);

        var controller = TtsController.Instance;
        if (controller == null) return;

        var after = new HashSet<BarPatron>(__instance.availablePatrons);
        foreach (var patron in before)
        {
            if (after.Contains(patron)) continue;
            // Patron rolled off the roster — drop its warmed audio
            if (BarPatronPatches.TryTakeLines(patron, out var lines))
            {
                foreach (var (speaker, text) in lines)
                    controller.DropCache(speaker, text);
            }
        }
    }
}
