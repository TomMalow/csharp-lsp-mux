namespace CsharpLspMux;

public sealed class MuxLogger(bool enabled, TextWriter writer)
{
    public bool IsEnabled { get; } = enabled;

    public void Log(string message)
    {
        if (IsEnabled)
            writer.WriteLine(message);
    }
}
