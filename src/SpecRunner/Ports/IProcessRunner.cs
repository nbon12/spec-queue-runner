namespace SpecRunner.Ports;

/// <summary>The outcome of one external process (stdout/stderr captured in full, FR-045).</summary>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public string Combined => string.IsNullOrEmpty(Stderr) ? Stdout : $"{Stdout}\n{Stderr}";
}

/// <summary>
/// The one boundary every external tool crosses — git, tmux, and claude are all invoked through
/// it (constitution §5). Arguments are passed per-item, never a concatenated shell string (§4).
/// Tests fake this; the real adapter drains both streams concurrently (research R6).
/// </summary>
public interface IProcessRunner
{
    /// <param name="fileName">Executable to run (e.g. <c>git</c>, <c>claude</c>).</param>
    /// <param name="arguments">Each argument separately — never a shell string.</param>
    /// <param name="workingDirectory">The item's worktree; never the shared clone (FR-012).</param>
    /// <param name="environment">Extra env vars; the child gets an explicit environment (R6).</param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default);
}
