using SpecRunner.Ports;

namespace SpecRunner.TestKit;

/// <summary>
/// A fake <see cref="IProcessRunner"/> that records every invocation (file, args, cwd, env) and
/// returns a scripted result. This is the C# analogue of the fake-claude binary — it captures
/// exactly what would have reached the real <c>claude</c>/<c>git</c>, which is what makes the
/// injection-canary property assertable (testing constitution §5): the canary must appear in no
/// recorded invocation.
/// </summary>
public sealed class RecordingProcessRunner : IProcessRunner
{
    public sealed record Invocation(
        string FileName,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory,
        IReadOnlyDictionary<string, string>? Environment);

    public List<Invocation> Invocations { get; } = [];

    /// <summary>Per-file scripted result; defaults to exit 0 with empty output.</summary>
    public Func<Invocation, ProcessResult> Respond { get; set; } =
        _ => new ProcessResult(0, string.Empty, string.Empty);

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var invocation = new Invocation(fileName, arguments, workingDirectory, environment);
        Invocations.Add(invocation);
        return Task.FromResult(Respond(invocation));
    }

    /// <summary>Every string that crossed the process boundary — argv + env values, all invocations.</summary>
    public IEnumerable<string> AllRecordedStrings()
    {
        foreach (var inv in Invocations)
        {
            yield return inv.FileName;
            foreach (var arg in inv.Arguments)
            {
                yield return arg;
            }

            if (inv.Environment is not null)
            {
                foreach (var value in inv.Environment.Values)
                {
                    yield return value;
                }
            }
        }
    }
}
