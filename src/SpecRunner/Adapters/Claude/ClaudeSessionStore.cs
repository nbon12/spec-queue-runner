using SpecRunner.Domain;
using SpecRunner.Ports;

namespace SpecRunner.Adapters.Claude;

/// <summary>
/// Filesystem implementation of the conversation-id store. Claude Code writes one
/// <c>&lt;conversation-id&gt;.jsonl</c> transcript per session under
/// <c>&lt;projectsRoot&gt;/&lt;encoded-worktree&gt;/</c>; the newest one names the live session to
/// resume (FR-022). Reads only — the runner never writes into Claude Code's store.
/// </summary>
public sealed class ClaudeSessionStore(string projectsRoot) : IClaudeSessionStore
{
    public string? NewestConversationId(string worktreePath)
    {
        var dir = Path.Combine(projectsRoot, LiveSession.ProjectDirName(worktreePath));
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var newest = new DirectoryInfo(dir)
            .GetFiles("*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        return newest is null ? null : Path.GetFileNameWithoutExtension(newest.Name);
    }
}
