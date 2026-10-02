using System.Collections.Concurrent;

namespace StorageInventory.Library.Tests;

/// <summary>A test-only directory watcher (A-26): records every create, delete and rename in the Library directory by name, so that
/// each can be attributed to the interlock state in which it happened. It sees events that a before/after listing cannot (a journal
/// created and deleted within one operation).</summary>
internal sealed class SideFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentQueue<(long Tick, string Event)> _events = new();

    internal SideFileWatcher(string directory)
    {
        _watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 64 * 1024 };
        _watcher.Created += (_, e) => _events.Enqueue((Environment.TickCount64, "Created " + e.Name));
        _watcher.Deleted += (_, e) => _events.Enqueue((Environment.TickCount64, "Deleted " + e.Name));
        _watcher.Renamed += (_, e) => _events.Enqueue((Environment.TickCount64, $"Renamed {e.OldName} -> {e.Name}"));
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Waits for the watcher to deliver what it has (events arrive on another thread), then returns everything seen so far.</summary>
    internal List<string> Settle(int milliseconds = 400)
    {
        Thread.Sleep(milliseconds);
        return [.. _events.Select(e => e.Event)];
    }

    internal List<(long Tick, string Event)> Timed() => [.. _events];

    public void Dispose() => _watcher.Dispose();
}
