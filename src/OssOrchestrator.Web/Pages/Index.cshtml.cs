using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web.Pages;

public sealed class IndexModel(OrcDb db) : PageModel
{
    public StateDoc? State { get; private set; }
    public List<CommandDoc> Recent { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    /// <summary>The repo tab in the issues list (the repo name), or the first repo when none is chosen.</summary>
    public string? Tab { get; private set; }

    public bool StateIsStale => State is null || DateTimeOffset.UtcNow - State.At > TimeSpan.FromMinutes(10);

    public async Task OnGetAsync(string? tab)
    {
        Message = TempData["Message"] as string;
        try
        {
            State = await db.ReadStateAsync();
            Recent = await db.RecentCommandsAsync();
            Tab = tab ?? State?.Issues.FirstOrDefault()?.Repo;
        }
        catch (Exception e)
        {
            Error = $"Could not read the database: {e.Message}";
        }
    }

    public async Task<IActionResult> OnPostCommandAsync(string type, string? repo, int? issue, string? value, string? tab)
    {
        var (cmd, error) = CommandFactory.Build(type, repo, issue, value, User.Identity?.Name);
        if (cmd is null)
        {
            TempData["Message"] = error;
        }
        else
        {
            await db.QueueAsync(cmd);
            TempData["Message"] = $"Queued: {type}" + (repo is null ? "" : $" {repo}") + (issue is null ? "" : $" #{issue}");
        }
        return Redirect(string.IsNullOrEmpty(tab) ? "/" : $"/?tab={Uri.EscapeDataString(tab)}");
    }
}
