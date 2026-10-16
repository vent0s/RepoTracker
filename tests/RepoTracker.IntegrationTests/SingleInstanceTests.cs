using System.IO.Pipes;
using RepoTracker.Infrastructure.Runtime;

namespace RepoTracker.IntegrationTests;

public class SingleInstanceTests
{
    [Fact]
    public async Task ConcurrentLaunchesShareOnePrimaryAndEachActivationIsDelivered()
    {
        using var server = new PrimaryServer();
        using var secondary = new SingleInstance(server.Identity);
        Assert.False(secondary.IsPrimary);
        Assert.True(await secondary.ActivatePrimaryAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await secondary.ActivatePrimaryAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, server.Activations);
    }

    [Fact]
    public async Task UnresponsivePrimaryHasBoundedTimeout()
    {
        using var server = new PrimaryServer(listen: false);
        using var secondary = new SingleInstance(server.Identity);
        Assert.False(await secondary.ActivatePrimaryAsync(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task CancelledActivationPropagatesCancellation()
    {
        using var server = new PrimaryServer(listen: false);
        using var secondary = new SingleInstance(server.Identity);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondary.ActivatePrimaryAsync(TimeSpan.FromSeconds(5), cancel.Token));
    }

    [Fact]
    public async Task IdleClientCannotPermanentlyBlockActivation()
    {
        using var server = new PrimaryServer();
        using var idle = new NamedPipeClientStream(".", server.Identity, PipeDirection.InOut, PipeOptions.Asynchronous);
        await idle.ConnectAsync(5000);
        await Task.Delay(2300);
        using var secondary = new SingleInstance(server.Identity);
        Assert.True(await secondary.ActivatePrimaryAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, server.Activations);
    }

    [Fact]
    public void ShutdownReleasesMutexAndStopsWaitingListener()
    {
        var server = new PrimaryServer();
        var identity = server.Identity;
        server.Dispose();
        using var replacement = new SingleInstance(identity);
        Assert.True(replacement.IsPrimary);
    }

    [Fact]
    public void ProfileIdentityIsCaseInsensitiveAndDoesNotExposePath()
    {
        var first = InstanceIdentity.ForProfile(@"E:\Tests\RepoTracker\profile\");
        Assert.Equal(first, InstanceIdentity.ForProfile(@"e:\tests\repotracker\profile"));
        Assert.NotEqual(first, InstanceIdentity.ForProfile(@"E:\Tests\RepoTracker\other"));
        Assert.DoesNotContain("Tests", first);
    }

    private sealed class PrimaryServer : IDisposable
    {
        private readonly ManualResetEventSlim ready = new();
        private readonly ManualResetEventSlim stop = new();
        private readonly Thread thread;
        private int activations;
        private Exception? failure;
        public int Activations => Volatile.Read(ref activations);
        public string Identity { get; } = "RepoTracker-test-" + Guid.NewGuid().ToString("N");

        public PrimaryServer(bool listen = true)
        {
            thread = new Thread(() =>
            {
                try
                {
                    using var instance = new SingleInstance(Identity);
                    if (!instance.IsPrimary) throw new InvalidOperationException("Test profile collision.");
                    if (listen) instance.Listen(() => Interlocked.Increment(ref activations));
                    ready.Set();
                    stop.Wait();
                }
                catch (Exception error) { failure = error; ready.Set(); }
            }) { IsBackground = true };
            thread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
        }

        public void Dispose()
        {
            stop.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The pipe listener did not stop.");
            ready.Dispose();
            stop.Dispose();
            Assert.Null(failure);
        }
    }
}
