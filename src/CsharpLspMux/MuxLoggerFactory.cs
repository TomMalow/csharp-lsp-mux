namespace CsharpLspMux;

public static class MuxLoggerFactory
{
    /// <summary>
    /// Creates a MuxLogger from configuration inputs.
    /// Precedence: flag (stderr) > LSP_MUX_LOG_FILE (file) > LSP_MUX_LOG=debug (stderr) > disabled.
    /// The --log-level debug flag always writes to stderr, ignoring LSP_MUX_LOG_FILE.
    /// </summary>
    public static (MuxLogger? Logger, StreamWriter? FileWriter) Create(
        string? logFilePath,
        bool debugFlagEnabled,
        bool debugEnvEnabled,
        TextWriter stderr)
    {
        if (debugFlagEnabled)
            return (new MuxLogger(enabled: true, stderr), null);

        if (logFilePath is not null)
        {
            try
            {
                var fileWriter = new StreamWriter(logFilePath, append: true);
                return (new MuxLogger(enabled: true, fileWriter), fileWriter);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"[mux] warning: cannot open log file '{logFilePath}': {ex.Message} — falling back to stderr");
                return (new MuxLogger(enabled: true, stderr), null);
            }
        }

        if (debugEnvEnabled)
            return (new MuxLogger(enabled: true, stderr), null);

        return (null, null);
    }
}
