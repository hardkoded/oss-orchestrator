using System.Diagnostics;

namespace OssOrchestrator.Agent;

/// <summary>
/// Tells gh which repo a call is about. On the user's PC gh finds it from the repo folder. Where the folder does not
/// exist (the cloud job), GH_REPO names the repo instead, so no clone is needed.
/// </summary>
public static class GhEnv
{
    private static readonly Dictionary<string, string> RepoByFolder = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(ConfigDoc config)
    {
        foreach (var r in config.Repos) RepoByFolder[r.Folder] = r.Repo;
    }

    public static void Apply(ProcessStartInfo psi, string folder)
    {
        if (Directory.Exists(folder)) psi.WorkingDirectory = folder;
        else if (RepoByFolder.TryGetValue(folder, out var repo)) psi.Environment["GH_REPO"] = repo;
    }
}
