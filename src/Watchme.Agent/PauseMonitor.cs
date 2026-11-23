using System.Text.Json;
using Watchme.Core;

namespace Watchme.Agent;

/// <summary>
/// Tells the trackers whether <c>watchme pause</c> is in effect. It reads <c>pause.json</c> again
/// only when the file changes, and when a pause runs out it removes the file and records the
/// resume at the pause's end time, so tracking picks up again by itself.
/// </summary>
internal sealed class PauseMonitor(string path, Action<WatchEvent> emit)
{
    private readonly Lock _gate = new();
    private DateTime _stamp = DateTime.MinValue;
    private PauseState.Pause? _pause;

    /// <summary>Called by the focus and process trackers, from their own threads.</summary>
    public bool IsPaused(long now)
    {
        lock (_gate)
        {
            Refresh();
            if (_pause is null)
                return false;
            if (now < _pause.Since)
                return false;
            if (now < _pause.Until)
                return true;

            Log.Info("Pause ended; recording again.");
            emit(new WatchEvent(_pause.Until, EventSources.Session, "resume") { Data = JsonSerializer.Serialize(new { early = false }) });
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Could not remove the pause file", ex); }
            _pause = null;
            _stamp = DateTime.MinValue;
            return false;
        }
    }

    private void Refresh()
    {
        DateTime stamp;
        try
        {
            stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (IOException)
        {
            return;
        }
        if (stamp == _stamp)
            return;
        _stamp = stamp;
        var before = _pause;
        _pause = stamp == DateTime.MinValue ? null : PauseState.Read(path);
        if (_pause is not null && before?.Until != _pause.Until)
            Log.Info($"Paused until {DateTimeOffset.FromUnixTimeMilliseconds(_pause.Until).LocalDateTime:HH:mm}.");
        else if (_pause is null && before is not null)
            Log.Info("Resumed.");
    }
}
