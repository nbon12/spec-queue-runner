using System.Diagnostics;
using SpecRunner.Ports;

namespace SpecRunner.Adapters;

/// <summary>
/// The real process boundary (research R6): <see cref="ProcessStartInfo.ArgumentList"/> so
/// prompts with quotes/newlines/backticks are safe (§4); stdout and stderr drained on separate
/// tasks so a large headless run can't deadlock on a full pipe buffer; and an explicitly
/// constructed environment so the child never inherits the operator's <c>claude</c> shell alias
/// (which carries <c>--dangerously-skip-permissions</c>).
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain both streams concurrently: reading one to completion before the other deadlocks
        // once output exceeds the pipe buffer, which a headless run will do.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
