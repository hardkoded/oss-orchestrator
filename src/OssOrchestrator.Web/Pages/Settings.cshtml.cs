using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web.Pages;

public sealed class SettingsModel(OrcDb db) : PageModel
{
    public StateDoc? State { get; private set; }
    public ConfigDoc? Config { get; private set; }
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    public async Task OnGetAsync()
    {
        Message = TempData["Message"] as string;
        try
        {
            State = await db.ReadStateAsync();
            Config = await db.ReadConfigAsync();
        }
        catch (Exception e)
        {
            Error = $"Could not read the database: {e.Message}";
        }
    }

    public async Task<IActionResult> OnPostCommandAsync(string type, string? repo, int? issue, string? value)
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
        return RedirectToPage();
    }
}
