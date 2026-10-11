namespace MIG.Interfaces.Audio;

using System.Collections.Concurrent;

/// <summary>Control plane remains available while the audio engine is preparing a model.</summary>
internal sealed class AudioModelJob
{
    private readonly object sync = new();
    private CancellationTokenSource cancellation = new();
    private Task? task;
    private readonly ConcurrentDictionary<string, object> state = new();
    private readonly Action<string, object> emit;

    internal AudioModelJob(Action<string, object> emit) { this.emit = emit; }
    internal CancellationToken Token { get { lock (sync) return cancellation.Token; } }
    internal Dictionary<string, object> Snapshot() => new(state);
    internal void Publish(string property, object value) { state[property] = value; emit(property, value); }

    internal void Start(Func<CancellationToken, bool, bool> prepare, bool restart = false)
    {
        lock (sync)
        {
            if (task is { IsCompleted: false } && !cancellation.IsCancellationRequested && !restart) return;
            var previous = task;
            var oldCancellation = cancellation;
            oldCancellation.Cancel();
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            task = Task.Run(async () =>
            {
                if (previous != null) { try { await previous; } catch { } }
                oldCancellation.Dispose();
                if (!token.IsCancellationRequested) prepare(token, restart);
            });
        }
    }

    internal void Stop()
    {
        lock (sync)
        {
            if (state.TryGetValue("Download.State", out var value) && value.ToString() is "Downloading" or "Extracting")
                Publish("Download.State", "Stopping");
            cancellation.Cancel();
        }
    }
}
