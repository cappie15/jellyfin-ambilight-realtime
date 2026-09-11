namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Diagnostics;

public enum PluginActivityLevel
{
    Info,
    Warning,
}

public sealed record PluginActivityEntry(DateTimeOffset Timestamp, PluginActivityLevel Level, string Message);

/// <summary>
/// A small, bounded, in-memory record of this plugin's own noteworthy
/// events -- connects, disconnects, recoverable failures -- so the settings
/// page can show "what has this plugin actually been doing" without the
/// operator needing to find and read Jellyfin's own server log file, which
/// was reported as the settings page's one remaining blind spot: every other
/// piece of state (FPS, pill colours, controller status) is already visible
/// there, but nothing about *what just happened* was. Deliberately not a
/// mirror of every <c>ILogger</c> call in the plugin: only the handful of
/// call sites that answer "is it working right now" are wired to also call
/// this, chosen to match what an operator staring at the settings page
/// actually wants to know, not a full diagnostic trace.
/// </summary>
public sealed class PluginActivityLog
{
    public const int Capacity = 200;

    private readonly object _sync = new();
    private readonly Queue<PluginActivityEntry> _entries = new(Capacity);

    public void Info(string message) => Add(PluginActivityLevel.Info, message);

    public void Warning(string message) => Add(PluginActivityLevel.Warning, message);

    private void Add(PluginActivityLevel level, string message)
    {
        lock (_sync)
        {
            _entries.Enqueue(new PluginActivityEntry(DateTimeOffset.UtcNow, level, message));
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>Oldest first, matching how a log naturally reads top-to-bottom.</summary>
    public IReadOnlyList<PluginActivityEntry> Snapshot()
    {
        lock (_sync)
        {
            return [.. _entries];
        }
    }
}
