using Xunit;

namespace CsharpLspMux.Tests;

public class MuxLoggerFactoryTests
{
    [Fact]
    public void Off_ReturnsNullLogger()
    {
        var stderr = new StringWriter();

        var (logger, fileWriter) = MuxLoggerFactory.Create(LogLevel.Off, filePath: null, stderr);

        Assert.Null(logger);
        Assert.Null(fileWriter);
    }

    [Fact]
    public void Off_WithFilePath_ReturnsNullLogger()
    {
        var tempFile = Path.GetTempFileName();
        var stderr = new StringWriter();
        try
        {
            var (logger, fileWriter) = MuxLoggerFactory.Create(LogLevel.Off, filePath: tempFile, stderr);

            Assert.Null(logger);
            Assert.Null(fileWriter);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void Info_NoFile_ReturnsStderrLoggerAtInfoLevel()
    {
        var stderr = new StringWriter();

        var (logger, fileWriter) = MuxLoggerFactory.Create(LogLevel.Info, filePath: null, stderr);

        Assert.NotNull(logger);
        Assert.Null(fileWriter);
        Assert.True(logger!.IsInfoEnabled);
        Assert.False(logger.IsDebugEnabled);
        logger.Info("info-test");
        Assert.Contains("info-test", stderr.ToString());
    }

    [Fact]
    public void Debug_NoFile_ReturnsStderrLoggerAtDebugLevel()
    {
        var stderr = new StringWriter();

        var (logger, fileWriter) = MuxLoggerFactory.Create(LogLevel.Debug, filePath: null, stderr);

        Assert.NotNull(logger);
        Assert.Null(fileWriter);
        Assert.True(logger!.IsInfoEnabled);
        Assert.True(logger.IsDebugEnabled);
        logger.Debug("debug-test");
        Assert.Contains("debug-test", stderr.ToString());
    }

    [Fact]
    public void Info_WithValidFile_WritesToFileNotStderr()
    {
        var tempFile = Path.GetTempFileName();
        var stderr = new StringWriter();
        try
        {
            var (logger, fileWriter) = MuxLoggerFactory.Create(LogLevel.Info, filePath: tempFile, stderr);

            using (fileWriter)
            {
                Assert.NotNull(logger);
                Assert.NotNull(fileWriter);
                logger!.Info("file-test");
            }

            Assert.Contains("file-test", File.ReadAllText(tempFile));
            Assert.Equal("", stderr.ToString());
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void Debug_WithValidFile_WritesToFile()
    {
        var tempFile = Path.GetTempFileName();
        var stderr = new StringWriter();
        try
        {
            var (logger, fileWriter) = MuxLoggerFactory.Create(LogLevel.Debug, filePath: tempFile, stderr);

            using (fileWriter)
            {
                Assert.NotNull(logger);
                logger!.Debug("debug-file-test");
            }

            Assert.Contains("debug-file-test", File.ReadAllText(tempFile));
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void Info_WithInvalidFile_WarnsAndFallsBackToStderr()
    {
        var badPath = Path.Combine("/nonexistent_dir_xyz", "mux.log");
        var stderr = new StringWriter();

        var (logger, fileWriter) = MuxLoggerFactory.Create(LogLevel.Info, filePath: badPath, stderr);

        Assert.NotNull(logger);
        Assert.Null(fileWriter);
        logger!.Info("fallback-test");
        var stderrText = stderr.ToString();
        Assert.Contains("fallback-test", stderrText);
        Assert.Contains("warning", stderrText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileWriter_HasAutoFlushEnabled()
    {
        var tempFile = Path.GetTempFileName();
        var stderr = new StringWriter();
        try
        {
            var (_, fileWriter) = MuxLoggerFactory.Create(LogLevel.Info, filePath: tempFile, stderr);

            using (fileWriter)
            {
                Assert.NotNull(fileWriter);
                Assert.True(fileWriter!.AutoFlush);
            }
        }
        finally { File.Delete(tempFile); }
    }
}
