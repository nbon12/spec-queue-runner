using SpecRunner.Ports;

namespace SpecRunner.Adapters.Tmux;

/// <summary>
/// tmux management for live sessions (technical constraints): new-session -d, send-keys, has-
/// session, kill-session, all through the process boundary. tmux state is disposable — losing it
/// costs a pause, nothing more (FR-047).
/// </summary>
public sealed class TmuxSessions(IProcessRunner processes)
{
    public async Task NewDetachedAsync(string session, string workingDirectory, CancellationToken ct = default) =>
        await Run(ct, "new-session", "-d", "-s", session, "-c", workingDirectory).ConfigureAwait(false);

    public async Task<bool> HasSessionAsync(string session, CancellationToken ct = default)
    {
        var r = await processes.RunAsync("tmux", ["has-session", "-t", session], ct: ct).ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    public async Task SendKeysAsync(string session, string text, CancellationToken ct = default)
    {
        await Run(ct, "send-keys", "-t", session, "-l", text).ConfigureAwait(false);
        await Run(ct, "send-keys", "-t", session, "Enter").ConfigureAwait(false);
    }

    public async Task<string> CapturePaneAsync(string session, CancellationToken ct = default)
    {
        var r = await processes.RunAsync("tmux", ["capture-pane", "-p", "-t", session], ct: ct)
            .ConfigureAwait(false);
        return r.Stdout;
    }

    public async Task KillSessionAsync(string session, CancellationToken ct = default) =>
        await processes.RunAsync("tmux", ["kill-session", "-t", session], ct: ct).ConfigureAwait(false);

    private async Task Run(CancellationToken ct, params string[] args)
    {
        var r = await processes.RunAsync("tmux", args, ct: ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException($"tmux {string.Join(' ', args)} failed: {r.Combined}");
        }
    }
}
