using SpecRunner.Ports;

namespace SpecRunner.TestKit;

/// <summary>
/// A fake conversation-id store for Tiers 2–3 — the live orchestration can be exercised without a
/// real Claude Code install. Map a worktree to the id its (pretend) session would have; a worktree
/// with no mapping reads back null, as if no session has initialized there yet.
/// </summary>
public sealed class InMemoryClaudeSessionStore : IClaudeSessionStore
{
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

    public void Set(string worktreePath, string conversationId) => _ids[worktreePath] = conversationId;

    public string? NewestConversationId(string worktreePath) =>
        _ids.TryGetValue(worktreePath, out var id) ? id : null;
}
