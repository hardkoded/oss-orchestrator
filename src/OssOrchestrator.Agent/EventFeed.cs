namespace OssOrchestrator.Agent;

/// <summary>
/// Reads the GitHub events the web app's webhook records in Cosmos. One consumer, so a plain pull of the change
/// feed is enough: no lease container, no processor host. The position is saved as soon as it moves, so a restart
/// replays at most an event it already woke for, never loses one. <see cref="StartAsync"/> must run before the
/// first pass, so events that arrive during that pass are not skipped.
/// </summary>
public sealed class EventFeed(DbStore db, TextWriter log)
{
    private string? _token;

    /// <summary>Loads the saved position, or marks "now" on a fresh install.</summary>
    public async Task StartAsync()
    {
        _token = await db.LoadFeedTokenAsync();
        if (_token is not null) return;
        _token = (await db.ReadEventsAsync(null)).Token;
        await db.SaveFeedTokenAsync(_token);
    }

    /// <summary>The events that arrived since the last call.</summary>
    public async Task<IReadOnlyList<EventDoc>> NewEventsAsync()
    {
        var (events, token) = await db.ReadEventsAsync(_token);
        _token = token;
        // A failed save must not hide the wake-up: the worst case is that a restart replays these events.
        if (events.Count > 0)
        {
            try { await db.SaveFeedTokenAsync(token); }
            catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not save the event position: {e.Message}"); }
        }
        return events;
    }
}
