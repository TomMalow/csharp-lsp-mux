using Xunit;

namespace CsharpLspMux.Tests;

public class MuxLoggerTests
{
    [Fact]
    public void Disabled_Log_WritesNothing()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(enabled: false, writer);

        logger.Log("should not appear");

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public void Enabled_Log_WritesMessageWithNewline()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(enabled: true, writer);

        logger.Log("[mux] hello");

        Assert.Equal("[mux] hello" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void IsEnabled_ReflectsConstructorArg()
    {
        var w = new StringWriter();
        Assert.False(new MuxLogger(enabled: false, w).IsEnabled);
        Assert.True(new MuxLogger(enabled: true, w).IsEnabled);
    }
}
