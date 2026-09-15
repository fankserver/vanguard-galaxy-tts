using VGModAPI;

namespace VGTTS.Dialogue;

/// <summary>
/// Playback surface the <see cref="DialogueTtsCoordinator"/> drives. Implemented by
/// <c>TtsController</c> in production and by a recording fake in the subscription /
/// cancellation / ownership tests, keeping the coordinator free of Unity dependencies.
/// </summary>
internal interface ITtsDialoguePlayer
{
    /// <summary>
    /// Speak one acquired dialogue line. The coordinator passes the presentation lease
    /// so the player can honor its cancellation token while synth is in flight and
    /// re-check <see cref="IDialoguePresentation.IsCurrent"/> right before playback.
    /// </summary>
    void Speak(string speaker, string text, IDialoguePresentation? presentation);

    /// <summary>Interrupt any in-flight synthesis and stop current audio. Main-thread only.</summary>
    void Stop();
}
