namespace SpecRunner.Ports;

/// <summary>
/// Reads the resumable conversation identifier Claude Code persists for a worktree (FR-022/047).
/// The id is what <c>claude --resume</c> accepts — recorded on the issue so a later stateless tick
/// can re-establish the same live session rather than starting a new one. Behind a port so the
/// tick's live orchestration is testable without a real Claude Code install.
/// </summary>
public interface IClaudeSessionStore
{
    /// <summary>
    /// The newest conversation id for the given worktree, or null if none exists yet (the session
    /// hasn't initialized, or the transcript folder is absent/unreadable).
    /// </summary>
    string? NewestConversationId(string worktreePath);
}
