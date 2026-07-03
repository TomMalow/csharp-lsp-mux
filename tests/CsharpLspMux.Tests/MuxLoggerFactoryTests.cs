using Xunit;

namespace CsharpLspMux.Tests;

public class MuxLoggerFactoryTests
{
    [Fact]
    public void DebugFlag_NoFile_ReturnsStderrLogger()
    {
        var stderr = new StringWriter();

        var (logger, fileWriter) = MuxLoggerFactory.Create(
            logFilePath: null, debugFlagEnabled: true, debugEnvEnabled: false, stderr);

        Assert.NotNull(logger);
        Assert.True(logger!.IsInfoEnabled);
        Assert.Null(fileWriter);
        logger.Info("flag-test");
        Assert.Contains("flag-test", stderr.ToString());
    }

    [Fact]
    public void ValidLogFile_ReturnsFileLogger()
    {
        var tempFile = Path.GetTempFileName();
        var stderr = new StringWriter();
        try
        {
            var (logger, fileWriter) = MuxLoggerFactory.Create(
                logFilePath: tempFile, debugFlagEnabled: false, debugEnvEnabled: false, stderr);

            using (fileWriter)
            {
                Assert.NotNull(logger);
                Assert.True(logger!.IsInfoEnabled);
                Assert.NotNull(fileWriter);
                logger.Info("file-test");
            }

            var content = File.ReadAllText(tempFile);
            Assert.Contains("file-test", content);
            Assert.Equal("", stderr.ToString());
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void InvalidLogFile_WarnsOnStderr_FallsBackToStderrLogger()
    {
        var badPath = Path.Combine("/nonexistent_dir_xyz", "mux.log");
        var stderr = new StringWriter();

        var (logger, fileWriter) = MuxLoggerFactory.Create(
            logFilePath: badPath, debugFlagEnabled: false, debugEnvEnabled: false, stderr);

        Assert.NotNull(logger);
        Assert.True(logger!.IsInfoEnabled);
        Assert.Null(fileWriter);
        logger.Info("stderr-fallback");
        var stderrText = stderr.ToString();
        Assert.Contains("stderr-fallback", stderrText);
        Assert.Contains("warning", stderrText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DebugEnv_NoFile_ReturnsStderrLogger()
    {
        var stderr = new StringWriter();

        var (logger, fileWriter) = MuxLoggerFactory.Create(
            logFilePath: null, debugFlagEnabled: false, debugEnvEnabled: true, stderr);

        Assert.NotNull(logger);
        Assert.True(logger!.IsInfoEnabled);
        Assert.Null(fileWriter);
        logger.Info("env-test");
        Assert.Contains("env-test", stderr.ToString());
    }

    [Fact]
    public void Neither_ReturnsNullLogger()
    {
        var stderr = new StringWriter();

        var (logger, fileWriter) = MuxLoggerFactory.Create(
            logFilePath: null, debugFlagEnabled: false, debugEnvEnabled: false, stderr);

        Assert.Null(logger);
        Assert.Null(fileWriter);
    }

    [Fact]
    public void DebugFlag_IgnoresLogFile_WritesToStderr()
    {
        var tempFile = Path.GetTempFileName();
        var stderr = new StringWriter();
        try
        {
            var (logger, fileWriter) = MuxLoggerFactory.Create(
                logFilePath: tempFile, debugFlagEnabled: true, debugEnvEnabled: false, stderr);

            Assert.NotNull(logger);
            Assert.Null(fileWriter);
            logger!.Info("flag-wins");
            Assert.Contains("flag-wins", stderr.ToString());
            // file should be empty (flag ignores LSP_MUX_LOG_FILE)
            Assert.Equal(0, new FileInfo(tempFile).Length);
        }
        finally { File.Delete(tempFile); }
    }
}
