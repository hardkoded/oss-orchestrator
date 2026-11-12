using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web.Pages;

public sealed class IndexModel(OrcDb db) : PageModel
{
    // Only these commands can be queued from the page. The agent applies them.
    private static readonly string[] Allowed =
        ["pause", "resume", "pause_all", "resume_all", "auto", "ignore", "restore", "limit"];

    public StateDoc? State { get; private set; }
    public ConfigDoc? Config { get; private set; }
    public List<CommandDoc> Recent { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    public bool StateIsStale => State is null || DateTimeOffset.UtcNow - State.At > TimeSpan.FromMinutes(10);

    public async Task OnGetAsync()
    {
        await LoadAsync();
        Message = TempData["Message"] as string;
    }

    public async Task<IActionResult> OnPostCommandAsync(string type, string? repo, int? issue, string? value)
    {
        if (!Allowed.Contains(type))
        {
            TempData["Message"] = "That command is not allowed.";
            return RedirectToPage();
        }

        object? parsed = null;
        if (type == "auto") parsed = value == "true";
        if (type == "limit")
        {
            if (!int.TryParse(value, out var limit) || limit is < 1 or > 20)
            {
                TempData["Message"] = "Limit must be between 1 and 20.";
                return RedirectToPage();
            }
            parsed = limit;
        }

        var cmd = new CommandDoc
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Repo = repo,
            Issue = issue,
            Value = parsed,
            CreatedAt = DateTimeOffset.UtcNow,
            By = User.Identity?.Name,
        };
        await db.QueueAsync(cmd);
        TempData["Message"] = $"Queued: {type}" + (repo is null ? "" : $" {repo}") + (issue is null ? "" : $" #{issue}");
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        try
        {
            State = await db.ReadStateAsync();
            Config = await db.ReadConfigAsync();
            Recent = await db.RecentCommandsAsync();
        }
        catch (Exception e)
        {
            Error = $"Could not read the database: {e.Message}";
        }
    }
}
