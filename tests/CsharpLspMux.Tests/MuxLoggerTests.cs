using System.Text.RegularExpressions;
using Xunit;

namespace CsharpLspMux.Tests;

public class MuxLoggerTests
{
    // --- Level properties ---

    [Fact]
    public void Off_IsInfoEnabled_ReturnsFalse()
    {
        var logger = new MuxLogger(LogLevel.Off, new StringWriter());
        Assert.False(logger.IsInfoEnabled);
    }

    [Fact]
    public void Off_IsDebugEnabled_ReturnsFalse()
    {
        var logger = new MuxLogger(LogLevel.Off, new StringWriter());
        Assert.False(logger.IsDebugEnabled);
    }

    [Fact]
    public void Info_IsInfoEnabled_ReturnsTrue()
    {
        var logger = new MuxLogger(LogLevel.Info, new StringWriter());
        Assert.True(logger.IsInfoEnabled);
    }

    [Fact]
    public void Info_IsDebugEnabled_ReturnsFalse()
    {
        var logger = new MuxLogger(LogLevel.Info, new StringWriter());
        Assert.False(logger.IsDebugEnabled);
    }

    [Fact]
    public void Debug_IsInfoEnabled_ReturnsTrue()
    {
        var logger = new MuxLogger(LogLevel.Debug, new StringWriter());
        Assert.True(logger.IsInfoEnabled);
    }

    [Fact]
    public void Debug_IsDebugEnabled_ReturnsTrue()
    {
        var logger = new MuxLogger(LogLevel.Debug, new StringWriter());
        Assert.True(logger.IsDebugEnabled);
    }

    // --- Info() output ---

    [Fact]
    public void Info_AtInfoLevel_WritesTimestampAndPrefix()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Info, writer);

        logger.Info("hello world");

        var line = writer.ToString().TrimEnd('\r', '\n');
        // Format: [HH:mm:ss.fff] [mux] hello world
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[mux\] hello world$", line);
    }

    [Fact]
    public void Info_AtDebugLevel_WritesTimestampAndPrefix()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, writer);

        logger.Info("msg");

        var line = writer.ToString().TrimEnd('\r', '\n');
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[mux\] msg$", line);
    }

    [Fact]
    public void Info_AtOffLevel_WritesNothing()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Off, writer);

        logger.Info("should not appear");

        Assert.Equal("", writer.ToString());
    }

    // --- Debug() output ---

    [Fact]
    public void Debug_AtDebugLevel_WritesTimestampAndPrefix()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, writer);

        logger.Debug("detail");

        var line = writer.ToString().TrimEnd('\r', '\n');
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[mux\] detail$", line);
    }

    [Fact]
    public void Debug_AtInfoLevel_WritesNothing()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Info, writer);

        logger.Debug("should not appear");

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public void Debug_AtOffLevel_WritesNothing()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Off, writer);

        logger.Debug("should not appear");

        Assert.Equal("", writer.ToString());
    }
}
