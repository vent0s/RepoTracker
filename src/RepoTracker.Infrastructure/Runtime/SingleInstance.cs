using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepoTracker.Infrastructure.Runtime;

/// <summary>Acquire and Dispose on the same thread (the WPF application thread owns the mutex).</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private bool disposed;
    public bool IsPrimary { get; }

    public SingleInstance(string identity)
    {
        pipeName = identity;
        mutex = new Mutex(false, @"Local\" + identity);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public void Listen(Action activate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsPrimary || listener is not null) throw new InvalidOperationException("Only the primary instance can listen once.");
        listener = ListenAsync(activate, lifetime.Token);
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                var message = new byte[1];
                if (await pipe.ReadAsync(message, request.Token).ConfigureAwait(false) == 1 && message[0] == 1)
                {
                    activate(); // The UI adapter posts to Dispatcher; this callback must never block it.
                    await pipe.WriteAsync(new byte[] { 1 }, request.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException)
            {
                // Do not fall back to an unrestricted pipe when integrity levels or ACLs differ.
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> ActivatePrimaryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var primaryProcessId))
                AllowSetForegroundWindow(primaryProcessId);
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            var ack = new byte[1];
            return await pipe.ReadAsync(ack, deadline.Token).ConfigureAwait(false) == 1 && ack[0] == 1;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try { listener?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            if (IsPrimary) mutex.ReleaseMutex();
            mutex.Dispose();
            lifetime.Dispose();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
