using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VGModAPI;
using VGTTS.Dialogue;
using Xunit;

namespace VGTTS.Tests;

/// <summary>
/// Subscription, presentation-lease ownership, and cancellation behaviour of
/// <see cref="DialogueTtsCoordinator"/> against a fake <see cref="IDialogueService"/>
/// that mirrors VGModAPI.Core.DialogueService semantics: the current lease is
/// cancelled before observers are notified, only the first claimant of a
/// (conversationId, sequence) wins, and claims die with the line.
/// </summary>
public class DialogueTtsCoordinatorTests
{
    private const string Owner = "vgtts.test";

    private static DialogueTtsCoordinator Create(
        FakeDialogueService service,
        FakePlayer player,
        Func<string, string>? speakerResolver = null,
        Func<bool>? enabled = null)
        => new(service, player, speakerResolver ?? (s => s), enabled ?? (() => true),
               service.Info.Add, service.Warnings.Add, Owner);

    [Fact]
    public void Attach_RegisteredObserver_DisposeRemovesIt()
    {
        var svc = new FakeDialogueService();
        var coord = Create(svc, new FakePlayer());
        Assert.True(coord.TryAttach());
        Assert.Equal(1, svc.ObserverCount);

        coord.Dispose();
        Assert.Equal(0, svc.ObserverCount);
    }

    [Fact]
    public void Attach_Failure_DisablesDialoguePath()
    {
        var svc = new FakeDialogueService { ThrowOnSubscribe = true };
        var coord = Create(svc, new FakePlayer());
        Assert.False(coord.TryAttach());
        Assert.Equal(0, svc.ObserverCount);
        Assert.NotEmpty(svc.Warnings);
    }

    [Fact]
    public void PresentedLine_ClaimsLeaseWithSnapshotIdentity_AndSpeaks()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        Create(svc, player).TryAttach();

        var snapshot = svc.Present("Ricko", "Still need those canisters.");

        var spoken = Assert.Single(player.Spoken);
        Assert.Equal("Ricko", spoken.Speaker);
        Assert.Equal("Still need those canisters.", spoken.Text);
        // Ownership: we claim under our owner id, keyed by the snapshot's lease identity.
        Assert.Equal(new[] { $"{Owner}|{snapshot.Sequence}" }, svc.Claims);
        var lease = Assert.Single(svc.Leases);
        Assert.Equal(Owner, lease.OwnerId);
        Assert.False(lease.Cancelled);
        Assert.Same(lease, spoken.Lease);
    }

    [Fact]
    public void PresentedLine_AppliesSpeakerResolver()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        Create(svc, player, speakerResolver: s => s == "Alexandra" ? "captain_f1" : s).TryAttach();

        svc.Present("Alexandra", "Ready when you are.");
        svc.Present("Ricko", "Good luck out there.");

        Assert.Equal(new[] { "captain_f1|Ready when you are.", "Ricko|Good luck out there." },
            player.Spoken.Select(s => $"{s.Speaker}|{s.Text}"));
    }

    [Fact]
    public void RivalFirstClaimant_SkipsPlayback_AndNeverSpeaks()
    {
        var svc = new FakeDialogueService { RivalWins = true };
        var player = new FakePlayer();
        Create(svc, player).TryAttach();

        svc.Present("Ricko", "Still need those canisters.");

        Assert.Empty(player.Script);
        // We still attempted the claim exactly once for that line.
        Assert.Single(svc.Claims);
        Assert.Empty(svc.Leases);
        Assert.NotEmpty(svc.Info);
    }

    [Fact]
    public void SecondClaimOfSameLine_Loses()
    {
        var svc = new FakeDialogueService();
        Create(svc, new FakePlayer()).TryAttach();
        var snapshot = svc.Current ?? svc.Present("Ricko", "Hello.");

        // Directly exercise the first-claimant rule the fake inherits from Core:
        // the coordinator already claimed this sequence, a second attempt fails.
        Assert.Null(svc.TryAcquirePresentation("other-provider", snapshot.ConversationId, snapshot.Sequence));
    }

    [Fact]
    public void Disabled_SkipsClaimEntirely_SoRivalCanOwn()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        Create(svc, player, enabled: () => false).TryAttach();

        svc.Present("Ricko", "Still need those canisters.");

        Assert.Empty(svc.Claims); // must not even occupy the lease when disabled
        Assert.Empty(player.Script);
    }

    [Fact]
    public void UnavailableService_SkipsClaim()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        Create(svc, player).TryAttach();
        svc.RaiseAvailability(new ServiceAvailability(ServiceUnavailableReason.Disabled, "tests"));

        svc.Present("Ricko", "Still need those canisters.");

        Assert.Empty(svc.Claims);
        Assert.Empty(player.Spoken);
        // Losing availability mid-session must stop any playing audio (defensive).
        Assert.Equal(new[] { "STOP" }, player.Script);
    }

    [Fact]
    public void Replacement_CancelsOldLease_StopsThenSpeaksNew()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        Create(svc, player).TryAttach();

        svc.Present("Ricko", "Line one.");
        svc.Present("Ricko", "Line two.");

        // API contract: the old lease is cancelled (→ our Stop) before the new
        // snapshot reaches us, so audio order is stop-then-speak, never overlap.
        Assert.Equal(new[] { "SPEAK Ricko|Line one.", "STOP", "SPEAK Ricko|Line two." }, player.Script);
        Assert.Equal(2, svc.Leases.Count);
        Assert.True(svc.Leases[0].Cancelled);
        Assert.False(svc.Leases[1].Cancelled);
    }

    [Fact]
    public void Close_StopsAudio_AndDisposesLease()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        Create(svc, player).TryAttach();

        svc.Present("Ricko", "Farewell.");
        svc.Close();

        Assert.Equal(new[] { "SPEAK Ricko|Farewell.", "STOP" }, player.Script);
        var lease = Assert.Single(svc.Leases);
        Assert.True(lease.Cancelled);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void SessionReset_PresentsClosure_AndStops()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        Create(svc, player).TryAttach();

        svc.Present("Ricko", "Mid-conversation.");
        svc.ResetSession(); // Core Reset(session) → Close(): cancels leases, notifies Closed.

        Assert.Equal(new[] { "SPEAK Ricko|Mid-conversation.", "STOP" }, player.Script);
    }

    [Fact]
    public void StaleLeaseAtSpeakTime_NeverSpeaks_AndReleases()
    {
        var svc = new FakeDialogueService { ServeStaleLease = true };
        var player = new FakePlayer();
        Create(svc, player).TryAttach();

        svc.Present("Ricko", "Line replaced before we could speak it.");

        Assert.Empty(player.Spoken);
        var lease = Assert.Single(svc.Leases);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void EmptyText_NeverClaims_SoRivalCanOwn_WithoutSpeaking()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        Create(svc, player).TryAttach();

        svc.Present("Ricko", "   ");

        Assert.Empty(player.Spoken);
        // Whitespace lines must not occupy the sticky per-line claim (Core clears
        // it only on the next change, so a claimed-and-discarded line locks rivals out).
        Assert.Empty(svc.Claims);
        Assert.Empty(svc.Leases);
        Assert.Empty(player.Script);
    }

    [Fact]
    public async Task OffThreadCancellation_QueuesStop_UntilPump()
    {
        var svc = new FakeDialogueService();
        var player = new FakePlayer();
        var coord = Create(svc, player);
        coord.TryAttach();
        svc.Present("Ricko", "Line one.");

        // The API promises main-thread cancellation; if a lease is ever cancelled
        // elsewhere, the Unity-touching Stop must not run inline — only via Pump.
        await Task.Run(() => svc.Leases[0].CancelForReplacement());
        Assert.DoesNotContain("STOP", player.Script);

        coord.Pump();
        Assert.Contains("STOP", player.Script);
    }

    [Fact]
    public void CoordinatorDispose_ReleasesHeldLease()
    {
        var svc = new FakeDialogueService();
        var coord = Create(svc, new FakePlayer());
        coord.TryAttach();
        svc.Present("Ricko", "Still playing when the plugin unloads.");

        coord.Dispose();

        var lease = Assert.Single(svc.Leases);
        Assert.True(lease.Disposed);
        Assert.Equal(0, svc.ObserverCount);
    }

    // -------------------------------------------------------------------- fakes

    private sealed class FakePresentation : IDialoguePresentation
    {
        private readonly CancellationTokenSource _cts = new();
        public FakePresentation(string ownerId) => OwnerId = ownerId;
        public string OwnerId { get; }
        public bool IsCurrent { get; set; } = true;
        public bool Cancelled => _cts.IsCancellationRequested;
        public bool Disposed { get; private set; }
        public CancellationToken Cancellation => _cts.Token;
        /// <summary>Mirror Core's Change(): lease cancellation on replacement/close.</summary>
        public void CancelForReplacement() { if (!_cts.IsCancellationRequested) _cts.Cancel(); }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (!_cts.IsCancellationRequested) _cts.Cancel(); // Core disposes via cancel
        }
    }

    /// <summary>Faithful-enough stand-in for VGModAPI.Core.DialogueService semantics.</summary>
    private sealed class FakeDialogueService : IDialogueService
    {
        private readonly List<Action<DialogueSnapshot>> _observers = new();
        private readonly HashSet<(Guid, long)> _claims = new();
        private readonly List<FakePresentation> _leases = new();
        private Action<ServiceAvailability>? _availabilityChanged;
        private long _sequence;
        private Guid _conversation = Guid.NewGuid();

        public ServiceAvailability Availability { get; private set; } = ServiceAvailability.Available;
        public event Action<ServiceAvailability>? AvailabilityChanged
        {
            add => _availabilityChanged += value;
            remove => _availabilityChanged -= value;
        }
        public DialogueSnapshot? Current { get; private set; }
        public IStoryCharacterService Characters => throw new NotSupportedException("not part of the playback path");

        public bool ThrowOnSubscribe;
        public bool RivalWins;
        public bool ServeStaleLease;
        public int ObserverCount => _observers.Count;
        public List<string> Claims { get; } = new();
        public List<string> Info { get; } = new();
        public List<string> Warnings { get; } = new();
        public IReadOnlyList<FakePresentation> Leases => _leases;

        public IDisposable Subscribe(Action<DialogueSnapshot> observer)
        {
            if (ThrowOnSubscribe) throw new InvalidOperationException("observer limit reached");
            _observers.Add(observer);
            return new Subscription(this, observer);
        }

        public IDialoguePresentation? TryAcquirePresentation(string ownerId, Guid conversationId, long sequence)
        {
            Claims.Add($"{ownerId}|{sequence}");
            if (!Availability.IsAvailable) return null;
            if (Current is not { IsOpen: true }) return null;
            if (Current.ConversationId != conversationId || Current.Sequence != sequence) return null;
            if (RivalWins || !_claims.Add((conversationId, sequence))) return null; // first claimant wins, even after disposal
            var lease = new FakePresentation(ownerId) { IsCurrent = !ServeStaleLease };
            _leases.Add(lease);
            return lease;
        }

        public DialogueSnapshot Present(string speaker, string text)
        {
            var snapshot = new DialogueSnapshot(DialogueChange.LinePresented, null, _conversation, ++_sequence, speaker, text);
            CancelLiveLeases();
            Current = snapshot;
            Notify(snapshot);
            return snapshot;
        }

        public void Close()
        {
            if (Current == null) return;
            var snapshot = new DialogueSnapshot(DialogueChange.Closed, null, Current.ConversationId, ++_sequence, "", "");
            CancelLiveLeases();
            Current = null;
            Notify(snapshot);
        }

        /// <summary>Core Reset(session) closes the current conversation before swapping ids.</summary>
        public void ResetSession() => Close();

        public void RaiseAvailability(ServiceAvailability availability)
        {
            Availability = availability;
            _availabilityChanged?.Invoke(availability);
        }

        private void CancelLiveLeases()
        {
            foreach (var lease in _leases) lease.CancelForReplacement();
        }

        private void Notify(DialogueSnapshot snapshot)
        {
            foreach (var observer in _observers.ToArray()) observer(snapshot);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly FakeDialogueService _service;
            private readonly Action<DialogueSnapshot> _observer;
            public Subscription(FakeDialogueService service, Action<DialogueSnapshot> observer)
            {
                _service = service;
                _observer = observer;
            }
            public void Dispose() => _service._observers.Remove(_observer);
        }
    }

    private sealed class FakePlayer : ITtsDialoguePlayer
    {
        /// <summary>Ordered "SPEAK speaker|text" / "STOP" trace.</summary>
        public List<string> Script { get; } = new();
        public List<(string Speaker, string Text, IDialoguePresentation? Lease)> Spoken { get; } = new();

        public void Speak(string speaker, string text, IDialoguePresentation? presentation)
        {
            Script.Add($"SPEAK {speaker}|{text}");
            Spoken.Add((speaker, text, presentation));
        }

        public void Stop() => Script.Add("STOP");
    }
}
