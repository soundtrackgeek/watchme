using System.Text.Json;
using Watchme.Core;

namespace Watchme.Agent;

/// <summary>
/// Diffs a process snapshot every few seconds and emits process/start and process/exit
/// for processes in the current user session. Start times come from the OS, so they are exact;
/// exit times are accurate to the poll interval. No admin rights needed.
/// </summary>
internal sealed class ProcessTracker(WatchmeConfig config, Action<WatchEvent> emit)
{
    private sealed record Known(string Name, string? Exe, long? CreatedMs, uint ParentPid);

    private readonly Dictionary<uint, Known> _known = [];
    private readonly uint? _session = Native.GetSessionId((uint)Environment.ProcessId);
    private bool _baselineTaken;

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, config.ProcessPollSeconds)));
        try
        {
            do
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    Log.Error("Process poll failed", ex);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Poll()
    {
        var snapshot = Native.SnapshotProcesses();
        var names = snapshot.ToDictionary(p => p.Pid, p => p.Name);
        var seen = new HashSet<uint>();

        foreach (var p in snapshot)
        {
            if (p.Pid == 0 || Native.GetSessionId(p.Pid) != _session)
                continue;
            if (config.IgnoreProcesses.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            seen.Add(p.Pid);
            if (_known.ContainsKey(p.Pid))
                continue;

            var (exe, created) = Native.GetProcessDetails(p.Pid);
            var known = new Known(p.Name, exe, created, p.ParentPid);
            _known[p.Pid] = known;

            // The first snapshot is what was already running when the agent started; not "opened now".
            if (!_baselineTaken)
                continue;

            var data = JsonSerializer.Serialize(new
            {
                ppid = p.ParentPid,
                parent = names.GetValueOrDefault(p.ParentPid),
            });
            emit(new WatchEvent(created ?? Clock.NowMs(), EventSources.Process, "start")
            {
                App = p.Name,
                Pid = (int)p.Pid,
                Exe = exe,
                Data = data,
            });
        }

        foreach (var (pid, known) in _known.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            _known.Remove(pid);
            var now = Clock.NowMs();
            var data = known.CreatedMs is { } c
                ? JsonSerializer.Serialize(new { runtime_s = (now - c) / 1000 })
                : null;
            emit(new WatchEvent(now, EventSources.Process, "exit")
            {
                App = known.Name,
                Pid = (int)pid,
                Exe = known.Exe,
                Data = data,
            });
        }

        _baselineTaken = true;
    }
}
