namespace CsharpLspMux;

public static class MuxLoggerFactory
{
    /// <summary>
    /// Creates a <see cref="MuxLogger"/> from a resolved log level and optional file path.
    /// Returns null when <paramref name="level"/> is <see cref="LogLevel.Off"/>.
    /// When <paramref name="filePath"/> is provided, writes to that file (AutoFlush=true);
    /// falls back to <paramref name="stderr"/> with a warning on open failure.
    /// </summary>
    public static (MuxLogger? Logger, StreamWriter? FileWriter) Create(
        LogLevel level,
        string? filePath,
        TextWriter stderr)
    {
        if (level == LogLevel.Off)
            return (null, null);

        if (filePath is not null)
        {
            try
            {
                var fileWriter = new StreamWriter(filePath, append: true) { AutoFlush = true };
                return (new MuxLogger(level, fileWriter), fileWriter);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"[mux] warning: cannot open log file '{filePath}': {ex.Message} — falling back to stderr");
                return (new MuxLogger(level, stderr), null);
            }
        }

        return (new MuxLogger(level, stderr), null);
    }
}
