using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web.Pages;

public sealed class IndexModel(OrcDb db, IMemoryCache cache) : PageModel
{
    public StateDoc? State { get; private set; }
    public ConfigDoc? Config { get; private set; }
    public List<CommandDoc> Recent { get; private set; } = [];
    public List<CommandDoc> Pending { get; private set; } = [];
    public SessionBoard Board { get; private set; } = SessionGroups.Build([], []);
    public List<HistoryPoint> History { get; private set; } = [];
    public string? HistoryError { get; private set; }
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    public bool StateIsStale => State is null || DateTimeOffset.UtcNow - State.At > TimeSpan.FromMinutes(10);

    /// <summary>True while one of these commands waits for the agent. A null repo or issue matches any.</summary>
    public bool IsPending(string? repo, int? issue, params string[] types) =>
        Pending.Any(c => types.Contains(c.Type) && (repo is null || c.Repo == repo) && (issue is null || c.Issue == issue));

    public async Task OnGetAsync()
    {
        Message = TempData["Message"] as string;
        try
        {
            State = await db.ReadStateAsync();
            Config = await db.ReadConfigAsync();
            Recent = await db.RecentCommandsAsync();
            Pending = await db.PendingCommandsAsync(State?.At ?? DateTimeOffset.MinValue);
            Board = SessionGroups.Build(State?.Sessions ?? [], State?.Issues ?? []);
        }
        catch (Exception e)
        {
            Error = $"Could not read the database: {e.Message}";
            // The script keeps what is on screen when a refresh does not succeed.
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        // Apart from the rest, so a history failure leaves the dashboard working. The page reloads every 15 seconds,
        // and the history changes slowly, so it is read every 5 minutes. A failed read is not kept.
        try
        {
            History = await cache.GetOrCreateAsync("history", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                var now = DateTimeOffset.UtcNow;
                return HistoryChart.Points(await db.HistoryAsync(now.AddDays(-HistoryChart.Days)), now);
            }) ?? [];
        }
        catch (Exception e)
        {
            HistoryError = $"Could not read the history: {e.Message}";
        }
    }

    public async Task<IActionResult> OnPostCommandAsync(string? cmd)
    {
        try
        {
            var (commands, error) = CommandFactory.FromButton(cmd, await db.ReadConfigAsync(), User.Identity?.Name);
            foreach (var command in commands) await db.QueueAsync(command);
            TempData["Message"] = error
                ?? (commands.Count == 0 ? "Nothing to change." : $"Sent: {string.Join(", ", commands.Select(Activity.Describe))}.");
        }
        catch (Exception e)
        {
            TempData["Message"] = $"Could not send the command: {e.Message}";
        }
        return RedirectToPage();
    }
}
