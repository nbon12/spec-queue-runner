using System.Text;
using SpecRunner.Configuration;

namespace SpecRunner.Logging;

/// <summary>
/// The instance log (T046, previously unbuilt — the config's <c>log</c> field was read by nothing
/// and no file was ever written, so the only record was whatever the host captured from stdout).
///
/// Every line is timestamped and tee'd to two places for two different readers:
/// <list type="bullet">
///   <item>stdout — so <c>docker logs -f</c> shows a live supervisor's activity;</item>
///   <item>the configured file inside the instance volume — so history survives the container.</item>
/// </list>
/// The file rolls at a size cap, because the previous arrangement grew without bound. Logging
/// MUST NOT be able to break a tick: if the file cannot be written, the console still gets the
/// line and the failure is reported once rather than every call.
/// </summary>
public sealed class TickLog : TextWriter
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly TextWriter _console;
    private readonly string? _path;
    private readonly StringBuilder _line = new();
    private readonly object _gate = new();
    private bool _fileBroken;

    private TickLog(TextWriter console, string? path)
    {
        _console = console;
        _path = path;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public static TickLog For(InstanceConfig config, TextWriter console) =>
        new(console, string.IsNullOrWhiteSpace(config.Log) ? null : config.Log);

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                Emit(_line.ToString().TrimEnd('\r'));
                _line.Clear();
                return;
            }

            _line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        foreach (var c in value)
        {
            Write(c);
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value ?? string.Empty);
        Write('\n');
    }

    private void Emit(string message)
    {
        var stamped = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
        _console.WriteLine(stamped);
        _console.Flush(); // unbuffered: `docker logs -f` should show it now, not at exit

        if (_path is null || _fileBroken)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Roll();
            File.AppendAllText(_path, stamped + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _fileBroken = true;
            _console.WriteLine($"{stamped} [log file unavailable: {ex.Message} — console only from here]");
        }
    }

    private void Roll()
    {
        var info = new FileInfo(_path!);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var previous = _path + ".1";
        File.Delete(previous);          // keep one generation; this is a log, not an archive
        File.Move(_path!, previous);
    }
}
