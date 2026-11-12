using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OssOrchestrator.Agent;

/// <summary>
/// Reads a worker's terminal (hc read) to tell when it is waiting for a person. Herdr reports "idle" or "done" for
/// both a finished worker and one that asked a question, so the question is found in the screen text.
/// </summary>
public static partial class ScreenReader
{
    private const int TailLines = 60;

    // "QUESTION:" must start a line: the worker brief quotes the word in the middle of a sentence.
    [GeneratedRegex(@"(?m)^\s*QUESTION:|waiting for (your|an?) (answer|reply|decision|go-ahead|input)", RegexOptions.IgnoreCase)]
    private static partial Regex AskPattern();

    /// <summary>True when the last lines of the terminal show a question the worker is waiting on.</summary>
    public static async Task<bool> NeedsAnswerAsync(string terminal, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("hc") { RedirectStandardOutput = true };
        psi.ArgumentList.Add("read");
        psi.ArgumentList.Add(terminal);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("hc did not start");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) return false;
        var tail = string.Join('\n', stdout.Split('\n').TakeLast(TailLines));
        return AskPattern().IsMatch(tail);
    }
}
