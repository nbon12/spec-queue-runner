namespace SpecRunner.Ticking;

/// <summary>
/// Per-instance exclusive lock (FR-002, constitution §2): a <see cref="FileStream"/> with
/// <see cref="FileShare.None"/>, released by <c>using</c> even on unhandled exception. If held,
/// acquisition fails and the tick exits 0 immediately — overlapping ticks are normal. The OS
/// releases the handle on process death of any kind, so a killed tick never wedges the instance
/// (which the Tier 3 crash tests rely on).
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Acquire the lock, or null if another tick holds it.</summary>
    public static InstanceLock? TryAcquire(string lockPath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stream = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new InstanceLock(stream);
        }
        catch (IOException)
        {
            return null; // held by another tick
        }
    }

    public void Dispose() => _stream.Dispose();
}
