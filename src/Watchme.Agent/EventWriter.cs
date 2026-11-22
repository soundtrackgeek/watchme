using System.Threading.Channels;
using Watchme.Core;

namespace Watchme.Agent;

/// <summary>
/// The only code that touches the database. Collectors post events from their own threads;
/// this loop batches them into transactions and writes a heartbeat to meta.last_alive.
/// </summary>
internal sealed class EventWriter(Database db)
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly Channel<WatchEvent> _channel =
        Channel.CreateUnbounded<WatchEvent>(new UnboundedChannelOptions { SingleReader = true });

    public void Post(WatchEvent e) => _channel.Writer.TryWrite(e);

    /// <summary>No more events will be posted; the loop drains what is left and ends.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public async Task RunAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<WatchEvent>(64);
        var lastBeat = 0L;

        while (true)
        {
            bool more;
            using (var timeout = new CancellationTokenSource(HeartbeatInterval))
            {
                try
                {
                    more = await reader.WaitToReadAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    more = true; // Heartbeat interval passed with nothing to write.
                }
            }

            batch.Clear();
            while (batch.Count < 500 && reader.TryRead(out var e))
                batch.Add(e);

            try
            {
                db.Insert(batch);
                var now = Clock.NowMs();
                if (now - lastBeat >= HeartbeatInterval.TotalMilliseconds || !more)
                {
                    db.SetMeta("last_alive", now.ToString());
                    lastBeat = now;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to write {batch.Count} events", ex);
            }

            if (!more)
                return;
        }
    }
}
