namespace SpecRunner.Domain;

/// <summary>
/// Pure helpers for the live channel — session naming, the kickoff prompt, and reading the
/// recorded session id from an issue. The tmux/process side lives in the adapter; this is the
/// testable logic (FR-021/022).
/// </summary>
public static class LiveSession
{
    /// <summary>A deterministic tmux session name for an item.</summary>
    public static string TmuxName(int itemNumber) => $"spec-runner-{itemNumber}";

    /// <summary>
    /// The directory Claude Code stores a worktree's conversations under, relative to
    /// <c>~/.claude/projects</c>. Claude Code encodes the absolute working directory by replacing
    /// path separators with dashes (e.g. <c>/home/runner/work/42</c> → <c>-home-runner-work-42</c>),
    /// so the runner can find the conversation id by reading the newest transcript in that folder.
    /// (Encoding matches the probe transcript path; re-confirm in-container if a future Claude Code
    /// changes it — an unreadable folder just yields no id, which fails safe to the fallback.)
    /// </summary>
    public static string ProjectDirName(string worktreePath) =>
        worktreePath.Replace('/', '-');

    /// <summary>
    /// The kickoff scoped to resolving this item's open questions only (FR-021). For an execution
    /// block it additionally bars continuing implementation.
    /// </summary>
    public static string Kickoff(int itemNumber, bool isExecutionBlock)
    {
        var bar = isExecutionBlock
            ? " Do NOT continue implementation; only resolve the blocking decision and write the resolution into the plan."
            : string.Empty;
        return $"You are resolving the open questions for issue #{itemNumber} in this worktree. " +
               $"Write resolutions into the spec or plan files, not just the chat.{bar}";
    }

    /// <summary>The comment recording the live-session id (FR-022) — the only dialogue artifact.</summary>
    public static string SessionComment(string sessionId) =>
        $"{IdempotencyMarker.For("session", "live")}\nLive session: {sessionId}";

    /// <summary>Recover the recorded session id from an issue's comments, if present.</summary>
    public static string? RecordedId(IEnumerable<string> commentBodies)
    {
        foreach (var body in commentBodies)
        {
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Live session:", StringComparison.OrdinalIgnoreCase))
                {
                    var id = trimmed["Live session:".Length..].Trim();
                    if (id.Length > 0)
                    {
                        return id;
                    }
                }
            }
        }

        return null;
    }
}
