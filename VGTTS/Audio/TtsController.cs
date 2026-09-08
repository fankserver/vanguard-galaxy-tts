using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Behaviour.AudioSystem;
using Behaviour.Util;
using Source.AudioSystem;
using UnityEngine;
using VGTTS.Cache;
using VGTTS.Prerender;
using VGTTS.Text;
using VGTTS.TTS;
using VGTTS.Voice;

namespace VGTTS.Audio;

/// <summary>
/// Orchestrates the full pipeline: cache lookup → (synth if miss) → load clip → play.
/// Serializes one line at a time; a new <see cref="Speak"/> cancels whatever is in flight.
/// </summary>
internal sealed class TtsController
{
    public static TtsController? Instance { get; set; }

    private readonly KokoroProvider _provider;
    private readonly DiskCache _cache;
    private readonly VoiceMapper _voices;
    private readonly PrerenderLookup _prerender;
    private readonly UnprerenderedLog _unprerendered;
    private CancellationTokenSource? _currentCts;
    private SoundEmitter? _currentEmitter;
    private GameObject? _fallbackGo;

    /// <summary>
    /// In-flight synthesis dedupe. Keyed by cache path — if a warm task is
    /// already writing to a path, a concurrent Speak() call awaits it instead
    /// of starting a competing synth that would corrupt the output file.
    /// </summary>
    private readonly Dictionary<string, Task> _inflightSynths = new();
    private readonly object _inflightLock = new();

    private Task SynthDedup(string synthText, string voice, string path)
    {
        lock (_inflightLock)
        {
            if (_inflightSynths.TryGetValue(path, out var existing)) return existing;
            // Internal token is None so canceled callers don't kill the underlying synth.
            var task = _provider.SynthesizeAsync(synthText, voice, path, CancellationToken.None);
            _inflightSynths[path] = task;
            // Remove from map when done, regardless of outcome.
            task.ContinueWith(_ => { lock (_inflightLock) _inflightSynths.Remove(path); },
                TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }
    }

    public TtsController(KokoroProvider provider, DiskCache cache, VoiceMapper voices,
                         PrerenderLookup prerender, UnprerenderedLog unprerendered)
    {
        _provider = provider;
        _cache = cache;
        _voices = voices;
        _prerender = prerender;
        _unprerendered = unprerendered;
    }

    public void Speak(string speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Plugin.Instance == null) return;

        Stop();

        var cts = new CancellationTokenSource();
        _currentCts = cts;
        Plugin.Instance.StartCoroutine(SpeakCoroutine(speaker, text, cts.Token));
    }

    /// <summary>
    /// Ensure the disk cache holds a synth of (text, voice-for-speaker) without
    /// playing anything. Used by <see cref="CaptainNameCache"/> to pre-warm
    /// name-substituted dialogue lines on a background task so the first
    /// utterance in dialogue doesn't pay a synth penalty.
    /// </summary>
    internal BarWarmRetention.Lease RetainBarWarm(BarWarmRetention registry, string speaker, string text)
    {
        var synthText = TextNormalizer.ForTts(text);
        var voice = _voices.Resolve(speaker);
        var path = _cache.PathFor(synthText, voice, _voices.IsKnownSpeaker(speaker));
        return registry.Acquire(path, async () =>
        {
            if (!_cache.Exists(path)) await SynthDedup(synthText, voice, path).ConfigureAwait(false);
        }, () => { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); });
    }

    public async Task<WarmResult> WarmCacheAsync(string speaker, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return WarmResult.Skipped;
        var synthText = TextNormalizer.ForTts(text);
        var voice = _voices.Resolve(speaker);
        var persistent = _voices.IsKnownSpeaker(speaker);
        var path = _cache.PathFor(synthText, voice, persistent);
        if (_cache.Exists(path)) return WarmResult.AlreadyCached;
        await SynthDedup(synthText, voice, path).ConfigureAwait(false);
        return WarmResult.Synthesized;
    }

    /// <summary>
    /// Delete the cached WAV for (speaker, text). Used by lifecycle patches to
    /// evict audio for procedurally-named NPCs the moment they disappear from
    /// the game (bar patron refresh, POI removal, etc.). Best-effort — silent
    /// on failure, files get wiped on next plugin load either way.
    /// </summary>
    /// <summary>Seed the voice mapping for a procedural speaker so the
    /// gender-appropriate default kicks in before the first WarmCacheAsync
    /// call. No-op if already bound. See <see cref="Voice.VoiceMapper.Register"/>.</summary>
    public void RegisterVoice(string speaker, string voice) => _voices.Register(speaker, voice);

    public void DropCache(string speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var synthText = TextNormalizer.ForTts(text);
        var voice = _voices.Resolve(speaker);
        var persistent = _voices.IsKnownSpeaker(speaker);
        var path = _cache.PathFor(synthText, voice, persistent);
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Interrupt any in-flight synthesis and fade out any currently playing audio.
    /// Called on dialogue advance, dialogue close, and ECHO tip dismiss.
    /// </summary>
    public void Stop()
    {
        _currentCts?.Cancel();
        _currentCts = null;
        StopCurrent();
    }

    private IEnumerator SpeakCoroutine(string speaker, string text, CancellationToken ct)
    {
        var synthText = TextNormalizer.ForTts(text);

        // 1. Prerender path — premium baked audio, voice already chosen at build time.
        var prerenderedPath = _prerender.Resolve(synthText, speaker);
        if (prerenderedPath != null)
        {
            AudioClip? prerenderedClip = null;
            yield return AudioClipLoader.LoadOgg(prerenderedPath, c => prerenderedClip = c);
            if (prerenderedClip != null && !ct.IsCancellationRequested)
            {
                PlayClip(prerenderedClip);
                yield break;
            }
        }
        else
        {
            // Miss — log once per (speaker, text) so users can see at a glance which lines
            // fell through to live TTS and harvest them for the next render pass.
            // Captain-name substitution lines are warmed into DiskCache at save load
            // and WILL always miss the manifest by design — downgrade to Info for those
            // so users don't think something's broken.
            var key = PrerenderLookup.ComputeKey(synthText, speaker);
            if (_unprerendered.Record(speaker, synthText, key))
            {
                if (CaptainNameCache.IsWarmedLine(speaker, synthText))
                {
                    Plugin.Log.LogInfo($"[captain-warmed] {speaker}: \"{synthText}\" — playing from warm cache.");
                }
                else if (!_voices.IsKnownSpeaker(speaker))
                {
                    // Procedural NPC (bar patron, rescue victim, etc.) — can never
                    // be in the prerender pack by design; name varies per encounter.
                    // Live-synth path handles it; no harvest needed. INFO, not WARN.
                    Plugin.Log.LogInfo($"[procedural] {speaker}: \"{synthText}\" — live TTS (expected for procedurally-named NPCs).");
                }
                else
                {
                    Plugin.Log.LogWarning(
                        $"[prerender-miss] {speaker}: \"{synthText}\" — falling back to live TTS. " +
                        $"Harvest BepInEx/cache/VGTTS/unprerendered.tsv for the next render pass.");
                }
            }
        }

        // 2. Live fallback — Kokoro (or configured provider) with per-character voice mapping.
        // Persistent cache dir for named NPCs (kept across launches); session dir for
        // procedurally-named speakers (wiped on next plugin load so WAVs don't accumulate).
        var voice = _voices.Resolve(speaker);
        var persistent = _voices.IsKnownSpeaker(speaker);
        var path = _cache.PathFor(synthText, voice, persistent);

        if (!_cache.Exists(path))
        {
            // Await the existing warm synth if one's in flight, else start our own.
            // Either way the file at `path` is what we load next.
            var task = SynthDedup(synthText, voice, path);
            while (!task.IsCompleted)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return null;
            }
            if (task.IsFaulted)
            {
                Plugin.Log.LogError($"[tts] Kokoro synth failed: {task.Exception?.GetBaseException().Message}");
                yield break;
            }
        }

        if (ct.IsCancellationRequested) yield break;

        AudioClip? clip = null;
        yield return AudioClipLoader.LoadWav(path, c => clip = c);
        if (clip == null || ct.IsCancellationRequested) yield break;

        PlayClip(clip);
    }

    private void PlayClip(AudioClip clip)
    {
        StopCurrent();

        var mgr = PersistentSingleton<SoundManager>.Instance;
        if (mgr != null)
        {
            var soundData = new SoundData
            {
                clip = clip,
                volume = 1.0f,
                loop = false,
                playOnAwake = false,
                minPitch = 1.0f,
                maxPitch = 1.0f,
                frequentSound = false,
            };
            _currentEmitter = mgr.CreateSound().WithSoundData(soundData).PlayReturn();
            return;
        }

        // Fallback: plain AudioSource on a detached GameObject.
        _fallbackGo = new GameObject("VGTTS_Playback");
        Object.DontDestroyOnLoad(_fallbackGo);
        var src = _fallbackGo.AddComponent<AudioSource>();
        src.clip = clip;
        src.volume = 1.0f;
        src.Play();
        Object.Destroy(_fallbackGo, clip.length + 0.5f);
    }

    private void StopCurrent()
    {
        if (_currentEmitter != null)
        {
            try { _currentEmitter.Stop(); } catch { /* emitter may already be returning to pool */ }
            _currentEmitter = null;
        }

        if (_fallbackGo != null)
        {
            Object.Destroy(_fallbackGo);
            _fallbackGo = null;
        }
    }
}
