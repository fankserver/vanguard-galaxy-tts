using System;
using Source.Dialogues;
using Source.Personnel;
using Source.Player;

namespace VGTTS.Dialogue;

/// <summary>
/// Maps native dialogue speakers to the voice/prerender lookup keys. Shared by the
/// API-driven coordinator (name-string snapshots) and the read-only StartDialogue
/// prewarm hook (direct <see cref="Character"/> references).
/// </summary>
internal static class DialogueSpeaker
{
    /// <summary>
    /// Resolve from a live character reference (prewarm path). The player captain's
    /// Character.name is the user's chosen callsign/firstName, so we detect it by
    /// reference to <c>Characters.captain</c> and substitute a captain preset key.
    /// </summary>
    public static string Resolve(Character? character)
    {
        if (character == null) return "<unknown>";
        if (character == Characters.captain)
            return "captain_" + ResolveCaptainPreset();
        return character.name ?? "<unknown>";
    }

    /// <summary>
    /// Resolve from the API snapshot's resolved speaker name. The dialogue service
    /// exposes text snapshots without native object references, so captain identity
    /// falls back to an ordinal match against <c>Characters.captain.name</c> — the
    /// player's chosen callsign. A procedurally-named NPC wearing the exact same
    /// callsign is rare and would merely borrow the captain voice for that line.
    /// Secondary effects of such a false positive: the line is cached under the
    /// captain key in the persistent cache dir (surviving the session-cache wipe),
    /// name-keyed <c>DropCache</c> eviction for that NPC won't find the misfiled
    /// WAV, and the unprerendered log records it as a captain-warmed miss. Bounded
    /// hygiene noise only — no behavioral damage.
    /// </summary>
    public static string ResolveFromName(string? nativeSpeaker)
    {
        if (string.IsNullOrWhiteSpace(nativeSpeaker)) return "<unknown>";
        var captain = Characters.captain;
        if (captain != null && string.Equals(nativeSpeaker, captain.name, StringComparison.Ordinal))
            return "captain_" + ResolveCaptainPreset();
        return nativeSpeaker;
    }

    public static string ResolveCaptainPreset()
    {
        var cfg = Plugin.Instance.CfgCaptainPreset.Value?.ToLowerInvariant() ?? "auto";
        if (cfg is "m1" or "m2" or "m3" or "f1" or "f2" or "f3") return cfg;

        // "auto" (or any invalid value) — pick by commander gender.
        var isFemale = GamePlayer.current?.commander?.gender == Gender.Female;
        return isFemale ? "f1" : "m1";
    }
}
