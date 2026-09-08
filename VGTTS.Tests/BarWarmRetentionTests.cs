using System.Threading.Tasks;
using VGTTS.Audio;
using Xunit;

namespace VGTTS.Tests;

public sealed class BarWarmRetentionTests
{
    [Fact]
    public async Task RetiredPendingWarmCannotRecreateEvictedAudio()
    {
        var registry = new BarWarmRetention(); int runs = 0, evictions = 0;
        var lease = registry.Acquire("path", () => { runs++; return Task.CompletedTask; }, () => evictions++);
        lease.Dispose(); lease.Dispose(); await lease.Run();
        Assert.Equal(0, runs); Assert.Equal(1, evictions);
    }

    [Fact]
    public async Task InflightWarmFinishesBeforeLastOwnerEviction()
    {
        var registry = new BarWarmRetention(); int evictions = 0;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = registry.Acquire("path", () => finish.Task, () => evictions++);
        var running = lease.Run(); lease.Dispose();
        Assert.Equal(0, evictions);
        finish.SetResult(); await running;
        Assert.Equal(1, evictions);
    }

    [Fact]
    public async Task ReplacementOwnerProtectsSharedAudioFromRetiringWarm()
    {
        var registry = new BarWarmRetention(); int evictions = 0;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = registry.Acquire("same-path", () => finish.Task, () => evictions++);
        var running = first.Run(); first.Dispose();
        var replacement = registry.Acquire("same-path", () => Task.CompletedTask, () => evictions++);
        finish.SetResult(); await running;
        Assert.Equal(0, evictions);
        replacement.Dispose(); Assert.Equal(1, evictions);
    }

    [Fact]
    public async Task FailedWarmStillRetiresItsCacheLease()
    {
        var registry = new BarWarmRetention(); int evictions = 0;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = registry.Acquire("path", () => finish.Task, () => evictions++);
        var running = lease.Run(); lease.Dispose(); finish.SetException(new System.Exception("synth failure"));
        await Assert.ThrowsAsync<System.Exception>(() => running);
        Assert.Equal(1, evictions);
    }
}
