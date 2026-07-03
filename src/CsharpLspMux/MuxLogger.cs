namespace CsharpLspMux;

/// <summary>Log verbosity level for <see cref="MuxLogger"/>.</summary>
public enum LogLevel
{
    /// <summary>No output. All methods are no-ops.</summary>
    Off,
    /// <summary>Operational messages only. <see cref="MuxLogger.Debug"/> is suppressed.</summary>
    Info,
    /// <summary>Full verbosity. Both <see cref="MuxLogger.Info"/> and <see cref="MuxLogger.Debug"/> write output.</summary>
    Debug
}

public sealed class MuxLogger(LogLevel level, TextWriter writer)
{
    /// <summary>True when <see cref="Info"/> will produce output (level is Info or Debug).</summary>
    public bool IsInfoEnabled => level >= LogLevel.Info;

    /// <summary>True when <see cref="Debug"/> will produce output (level is Debug).</summary>
    public bool IsDebugEnabled => level >= LogLevel.Debug;

    /// <summary>Writes an info-level message. No-op when level is Off.</summary>
    public void Info(string message)
    {
        if (IsInfoEnabled)
            Write(message);
    }

    /// <summary>Writes a debug-level message. No-op when level is Info or Off.</summary>
    public void Debug(string message)
    {
        if (IsDebugEnabled)
            Write(message);
    }

    private void Write(string message) =>
        writer.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)}] [mux] {message}");
}
