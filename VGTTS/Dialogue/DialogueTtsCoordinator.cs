using System;
using System.Collections.Concurrent;
using System.Threading;
using VGModAPI;

namespace VGTTS.Dialogue;

/// <summary>
/// Canonical cooperative TTS ownership path: subscribes to
/// <see cref="IDialogueService.Subscribe"/> and speaks only lines whose presentation
/// lease it wins via <see cref="IDialogueService.TryAcquirePresentation"/>. The lease's
/// cancellation token — fired by the API on line replacement, window close, scene,
/// session or shutdown change — stops the audio. No Harmony hook speaks dialogue lines.
/// </summary>
internal sealed class DialogueTtsCoordinator : IDisposable
{
    private readonly IDialogueService _service;
    private readonly ITtsDialoguePlayer _player;
    private readonly Func<string, string> _speakerResolver;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly string _ownerId;
    private readonly int _mainThreadId;

    /// <summary>
    /// The API cancels leases on its main thread, so the token callback normally runs
    /// there. Should it ever arrive off-thread, we queue the Unity-touching Stop() for
    /// <see cref="Pump"/> on the plugin's Update instead of calling it inline.
    /// </summary>
    private readonly ConcurrentQueue<Action> _offThreadStops = new();

    private IDisposable? _subscription;
    private IDialoguePresentation? _held;
    private CancellationTokenRegistration _heldRegistration;
    private ServiceAvailability _lastAvailability;
    private bool _disposed;

    public DialogueTtsCoordinator(
        IDialogueService service,
        ITtsDialoguePlayer player,
        Func<string, string> speakerResolver,
        Func<bool> enabled,
        Action<string> info,
        Action<string> warn,
        string ownerId)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _speakerResolver = speakerResolver ?? throw new ArgumentNullException(nameof(speakerResolver));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
        _ownerId = string.IsNullOrWhiteSpace(ownerId) ? "vgtts" : ownerId;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _lastAvailability = _service.Availability;
    }

    /// <summary>
    /// Subscribe to the dialogue service. Must run on the Unity main thread after the
    /// API plugin's Awake (BepInEx dependency order guarantees the latter). Returns
    /// false when the service refuses the subscription — the caller must then leave
    /// dialogue playback disabled; there is deliberately no hook fallback.
    /// </summary>
    public bool TryAttach()
    {
        if (_disposed) return false;
        try
        {
            _subscription = _service.Subscribe(OnSnapshot);
            _service.AvailabilityChanged += OnAvailabilityChanged;
            if (!_lastAvailability.IsAvailable)
                _warn($"[dialogue] API dialogue service reports {_lastAvailability.Reason} — no lines will arrive until it becomes available.");
            return true;
        }
        catch (Exception ex)
        {
            _warn($"[dialogue] subscription failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Drain off-thread stop requests; call from the plugin's Update (main thread).</summary>
    public void Pump()
    {
        while (_offThreadStops.TryDequeue(out var stop))
        {
            try { _player.Stop(); }
            catch (Exception ex) { _warn($"[dialogue] deferred stop failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Invoked by the API on the main thread for every LinePresented/Closed snapshot.
    /// The API cancels the previous lease before notifying observers, so the incoming
    /// snapshot's predecessor has already been stopped via <see cref="OnLeaseCancelled"/>.
    /// </summary>
    private void OnSnapshot(DialogueSnapshot snapshot)
    {
        if (_disposed) return;
        ReleaseHeld();

        if (snapshot.Change == DialogueChange.Closed) return;

        // Disabled or unavailable → do not even claim: a rival cooperative provider
        // must still be able to win the line's presentation lease.
        if (!_enabled()) return;
        _lastAvailability = _service.Availability;
        if (!_lastAvailability.IsAvailable) return;

        var presentation = _service.TryAcquirePresentation(_ownerId, snapshot.ConversationId, snapshot.Sequence);
        if (presentation == null)
        {
            _info($"[dialogue] line {snapshot.ConversationId}/{snapshot.Sequence} owned by another provider or no longer current — skipping.");
            return;
        }

        _held = presentation;
        _heldRegistration = presentation.Cancellation.Register(static state => ((DialogueTtsCoordinator)state!).OnLeaseCancelled(), this);

        // The lease can already be stale if another observer re-entered the service
        // during notification delivery; docs require the IsCurrent re-check.
        if (!presentation.IsCurrent)
        {
            ReleaseHeld();
            return;
        }

        var text = snapshot.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            ReleaseHeld();
            return;
        }

        var speaker = _speakerResolver(snapshot.Speaker ?? string.Empty) ?? "<unknown>";
        _info($"[dialogue-api] {speaker}: \"{text}\"");
        _player.Speak(speaker, text, presentation);
    }

    /// <summary>
    /// Token callback — fires on line replacement, closure, scene unload, session
    /// change or API shutdown. The API runs these on the main thread; anything else is
    /// queued so Unity objects are only touched from there.
    /// </summary>
    private void OnLeaseCancelled()
    {
        // Keep _held: the next snapshot (or Dispose) still disposes the lease object.
        // The lease is already cancelled, so disposing it later is a harmless cleanup.
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            try { _player.Stop(); }
            catch (Exception ex) { _warn($"[dialogue] stop on cancellation failed: {ex.Message}"); }
        }
        else
        {
            _offThreadStops.Enqueue(_player.Stop);
        }
    }

    private void OnAvailabilityChanged(ServiceAvailability availability)
    {
        var wasAvailable = _lastAvailability.IsAvailable;
        _lastAvailability = availability;
        if (wasAvailable && !availability.IsAvailable)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) _player.Stop();
            else _offThreadStops.Enqueue(_player.Stop);
        }
    }

    /// <summary>
    /// Dispose the held lease and its cancellation registration. Never called from
    /// within the token callback (the API cancels synchronously before notifying us),
    /// so disposing the registration here cannot deadlock against a running callback.
    /// </summary>
    private void ReleaseHeld()
    {
        _heldRegistration.Dispose();
        _heldRegistration = default;
        var held = _held;
        _held = null;
        if (held == null) return;
        try { held.Dispose(); } catch (Exception ex) { _warn($"[dialogue] lease dispose failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _service.AvailabilityChanged -= OnAvailabilityChanged; } catch { /* fake services may no-op */ }
        ReleaseHeld();
        try { _subscription?.Dispose(); } catch { /* main-thread-only; best-effort on teardown */ }
        _subscription = null;
    }
}
