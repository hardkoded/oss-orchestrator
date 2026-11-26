using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web.Pages;

public sealed class IndexModel(OrcDb db) : PageModel
{
    public StateDoc? State { get; private set; }
    public ConfigDoc? Config { get; private set; }
    public List<CommandDoc> Recent { get; private set; } = [];
    public List<CommandDoc> Pending { get; private set; } = [];
    public SessionBoard Board { get; private set; } = SessionGroups.Build([], []);
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
