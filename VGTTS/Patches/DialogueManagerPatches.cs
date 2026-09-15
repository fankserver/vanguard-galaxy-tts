using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Behaviour.Dialogues;
using HarmonyLib;
using Source.Dialogues;
using VGTTS.Audio;
using VGTTS.Dialogue;
using VGTTS.Text;

namespace VGTTS.Patches;

/// <summary>
/// Read-only cache prewarm on <see cref="DialogueManager"/>'s <c>StartDialogue</c>.
///
/// Line playback, closure-stop and replacement-stop are owned exclusively by
/// <see cref="DialogueTtsCoordinator"/> through the VGModAPI dialogue service
/// (Subscribe + TryAcquirePresentation lease cancellation) — this class must never
/// Speak() or Stop() a presented line, or the old and new paths would double-play.
///
/// The prewarm hook stays because the API exposes no whole-conversation listing:
/// for procedural encounters (distress combat rescue, random salesmen, etc.) the
/// speaker name varies per encounter so those lines miss the prerender pack and the
/// first utterance would pay a ~1s synth delay. Sniffing the upcoming list here on a
/// background task has the first WAV ready by the time the service presents it, and
/// is genuinely needed per the migration brief's prewarm carve-out.
/// </summary>
[HarmonyPatch(typeof(DialogueManager))]
internal static class DialogueManagerPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(DialogueManager.StartDialogue))]
    private static void StartDialogue_Prefix(List<DialogueLine> dialogue)
    {
        if (dialogue == null || dialogue.Count == 0) return;
        var controller = TtsController.Instance;
        if (controller == null) return;
        if (Plugin.Instance == null || !Plugin.Instance.CfgEnabled.Value || !Plugin.Instance.CfgDialogue.Value) return;

        // Snapshot (speaker, text) pairs so the background task doesn't race
        // with the game mutating the list or the character objects.
        var pairs = new List<(string Speaker, string Text)>(dialogue.Count);
        foreach (var line in dialogue)
        {
            var speaker = DialogueSpeaker.Resolve(line?.character);
            var text = line?.text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text)) pairs.Add((speaker, text));
        }
        if (pairs.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var (speaker, text) in pairs)
            {
                try { await controller.WarmCacheAsync(speaker, TextNormalizer.ForTts(text), CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort; live-TTS path will retry on demand */ }
            }
        });
    }
}
