namespace OssOrchestrator.Agent;

/// <summary>A cloud worker session the agent tracks for an open issue. Status is "cloud", "blocked" while the worker waits for an answer, or "waiting" while it waits on another issue or PR.</summary>
public sealed record SessionInfo(string Repo, int Issue, string Status, string CloudSessionId, string CloudSessionUrl);
