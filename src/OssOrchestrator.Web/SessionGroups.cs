using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web;

/// <summary>Where a session stands, in the order the dashboard lists the groups.</summary>
public enum SessionGroup { NeedsAnswer, ReadyToMerge, Fixing, Working, Checks, Waiting }

/// <summary>A session, what it waits on, and the sessions that wait on it.</summary>
public sealed class SessionNode(SessionStatus session)
{
    public SessionStatus Session { get; } = session;
    public SessionGroup Group { get; } = SessionGroups.Of(session);
    /// <summary>What it waits on, in the worker's order. The node is null for a blocker that has no session.</summary>
    public List<(int Number, SessionNode? Node)> On { get; } = [];
    /// <summary>The session it is listed under: the one of its first blocker.</summary>
    public SessionNode? Blocker { get; set; }
    /// <summary>The sessions listed under this one.</summary>
    public List<SessionNode> Waiting { get; } = [];
    /// <summary>Every session that waits on this one, directly or through others, also by a blocker that is not its first.</summary>
    public IReadOnlyList<SessionNode> Held { get; set; } = [];
    /// <summary>True when this session is part of a ring of sessions that wait on each other.</summary>
    public bool InCycle { get; set; }
    public int Blocks => Held.Count;
    /// <summary>This session and all sessions under it.</summary>
    public IEnumerable<SessionNode> Tree => Waiting.SelectMany(w => w.Tree).Prepend(this);
}

/// <summary>
/// Waiting sessions whose blocker has no session. <paramref name="Number"/> is null when the worker did not name it, and
/// <paramref name="Issue"/> is null when the blocker is not in the open-issue list (it can be a PR).
/// </summary>
public sealed record OutsideBlocker(string Repo, int? Number, IssueStatus? Issue, IReadOnlyList<SessionNode> Waiting);

/// <summary>The sessions as trees: <paramref name="Roots"/> are listed in their own group, <paramref name="Outside"/> under Waiting.</summary>
public sealed record SessionBoard(IReadOnlyList<SessionNode> Roots, IReadOnlyList<OutsideBlocker> Outside, IReadOnlyList<SessionNode> All)
{
    /// <summary>The top-level sessions of a group. Needs answer lists the ones that block the most first, then the oldest.</summary>
    public IReadOnlyList<SessionNode> In(SessionGroup group) => Roots
        .Where(n => n.Group == group)
        .OrderByDescending(n => group == SessionGroup.NeedsAnswer ? n.Blocks : 0)
        .ThenBy(n => n.Session.StartedAt ?? DateTimeOffset.MaxValue)
        .ToList();

    public int Count(SessionGroup group) => All.Count(n => n.Group == group);
}

public static class SessionGroups
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromHours(10);

    /// <summary>The first rule that matches wins: a question, then a blocker, then the state of the PR.</summary>
    public static SessionGroup Of(SessionStatus s) => s.Status switch
    {
        "blocked" => SessionGroup.NeedsAnswer,
        "waiting" => SessionGroup.Waiting,
        _ => s.PrStatus switch
        {
            "failed" or "conflicted" => SessionGroup.Fixing,
            "ready" when !s.PrDraft => SessionGroup.ReadyToMerge,
            "building" when !s.PrDraft => SessionGroup.Checks,
            _ => SessionGroup.Working,
        },
    };

    /// <summary>A session that has written code for a long time and still has no PR.</summary>
    public static bool MayBeStuck(SessionStatus s, DateTimeOffset now) =>
        Of(s) == SessionGroup.Working && s.PrNumber is null && now - s.StartedAt > StuckAfter;

    /// <summary>Puts each waiting session under the session of its first blocker. A blocker is named by its issue or PR number.</summary>
    public static SessionBoard Build(IEnumerable<SessionStatus> sessions, IEnumerable<RepoIssues> issues)
    {
        var all = sessions.Select(s => new SessionNode(s)).ToList();
        foreach (var node in all.Where(n => n.Group == SessionGroup.Waiting))
        {
            foreach (var number in node.Session.Blockers)
            {
                var other = all.FirstOrDefault(o => o != node && o.Session.Repo == node.Session.Repo
                    && (o.Session.Issue == number || o.Session.PrNumber == number));
                // An issue and its PR name the same session, which is listed once.
                if (other is null || node.On.All(b => b.Node != other)) node.On.Add((number, other));
            }
            node.Blocker = node.On.FirstOrDefault().Node;
        }

        var waitsOnIt = all.SelectMany(n => n.On, (n, on) => (Waiter: n, on.Node)).ToLookup(e => e.Node, e => e.Waiter);
        foreach (var node in all)
        {
            var seen = new HashSet<SessionNode>();
            var held = new List<SessionNode>();
            var next = new Queue<SessionNode>(waitsOnIt[node]);
            while (next.TryDequeue(out var waiter))
            {
                if (!seen.Add(waiter)) continue;
                held.Add(waiter);
                foreach (var further in waitsOnIt[waiter]) next.Enqueue(further);
            }
            node.InCycle = held.Remove(node);
            node.Held = held;
        }

        // A ring of first blockers has no top. Cut it above its lowest issue, so each session is listed once.
        foreach (var node in all.OrderBy(n => n.Session.Issue))
        {
            var seen = new HashSet<SessionNode>();
            var next = node.Blocker;
            while (next is not null && next != node && seen.Add(next)) next = next.Blocker;
            if (next == node) node.Blocker = null;
        }

        foreach (var node in all) node.Blocker?.Waiting.Add(node);

        var tops = all.Where(n => n.Blocker is null).ToList();
        bool IsOutside(SessionNode n) => n.Group == SessionGroup.Waiting && n.On.FirstOrDefault().Node is null;
        var byRepo = issues.ToDictionary(r => r.Repo, r => r.Items);
        var outside = tops.Where(IsOutside)
            .GroupBy(n => (n.Session.Repo, Number: n.On.Count > 0 ? n.On[0].Number : (int?)null))
            .Select(g => new OutsideBlocker(g.Key.Repo, g.Key.Number,
                byRepo.GetValueOrDefault(g.Key.Repo)?.FirstOrDefault(i => i.Number == g.Key.Number), g.ToList()))
            .ToList();
        return new SessionBoard(tops.Where(n => !IsOutside(n)).ToList(), outside, all);
    }
}
