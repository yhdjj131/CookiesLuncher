using CookieLuncher.Core;

namespace CookieLuncher.Tests;

/// <summary>日志脱敏与清理测试。</summary>
public sealed class AppLoggerTests : TempRootTest
{
    [Theory]
    [InlineData("abcdef", "***")]
    [InlineData("abcdefg", "abcdef***")]
    [InlineData("abc", "***")]
    [InlineData("", "***")]
    public void MaskValue_ShouldMatchPythonBehaviour(string input, string expected)
        => Assert.Equal(expected, AppLogger.MaskValue(input));

    [Theory]
    [InlineData("SESSDATA=abc123def456", "SESSDATA=abc123***")]
    [InlineData("sessdata: abc123def456", "sessdata: abc123***")]
    [InlineData("\"bili_jct\":\"abc123def456\"", "\"bili_jct\":\"abc123***\"")]
    [InlineData("password = abc123def456", "password = abc123***")]
    [InlineData("token=short", "token=***")]
    [InlineData("nothing sensitive here", "nothing sensitive here")]
    [InlineData("", "")]
    public void Sanitize_ShouldMaskSensitiveValues(string input, string expected)
        => Assert.Equal(expected, AppLogger.Sanitize(input));

    [Fact]
    public void Sanitize_ShouldMaskCookieHeaderSegment()
    {
        // 与 Python 版正则逐字符一致（已用 Python 版的 sanitize 输出核对）：
        // "Cookie:" 的值部分会一直吃到分隔符，脱敏后保留前 6 位 "SESSDA"。
        var sanitized = AppLogger.Sanitize("Cookie: SESSDATA=abcdef123456; bili_jct=xyz7890000");

        Assert.Equal("Cookie: SESSDA***; bili_jct=xyz789***", sanitized);
    }

    [Fact]
    public void SetupAndInfo_ShouldWriteDailyLogFile()
    {
        AppLogger.Setup(AppPaths.LogDir, "INFO");

        AppLogger.Info("程序启动 SESSDATA=abcdef123456");
        AppLogger.Debug("这条不该写进文件");

        var path = Path.Combine(AppPaths.LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");

        // 日志文件在运行期被日志器持有写入句柄，读取前先关闭
        AppLogger.Shutdown();

        Assert.True(File.Exists(path));

        var content = File.ReadAllText(path);
        Assert.Contains("[INFO] 程序启动", content);
        Assert.Contains("SESSDATA=abcdef***", content);
        Assert.DoesNotContain("abcdef123456", content);
        Assert.DoesNotContain("这条不该写进文件", content);

        // 行格式：yyyy-MM-dd HH:mm:ss [LEVEL] message
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[INFO\] ", content);
    }

    [Fact]
    public void CleanupOldLogs_ShouldDeleteOnlyExpiredDatedFiles()
    {
        AppLogger.Setup(AppPaths.LogDir, "INFO");
        Directory.CreateDirectory(AppPaths.LogDir);

        var oldPath = Path.Combine(AppPaths.LogDir, "2000-01-01.log");
        var recentPath = Path.Combine(AppPaths.LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
        var otherPath = Path.Combine(AppPaths.LogDir, "notes.txt");
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(recentPath, "recent");
        File.WriteAllText(otherPath, "keep");

        var removed = AppLogger.CleanupOldLogs(30);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(recentPath));
        Assert.True(File.Exists(otherPath));

        AppLogger.Shutdown();
    }

    [Fact]
    public void WarningOrAbove_ShouldBeRaisedForWarnings()
    {
        AppLogger.Setup(AppPaths.LogDir, "WARNING");
        var received = new List<string>();
        void Handler(string line) => received.Add(line);

        AppLogger.WarningOrAbove += Handler;
        try
        {
            AppLogger.Info("info 不该触发");
            AppLogger.Warning("warning 应触发");
            AppLogger.Error("error 应触发");
        }
        finally
        {
            AppLogger.WarningOrAbove -= Handler;
            AppLogger.Shutdown();
        }

        Assert.Equal(2, received.Count);
        Assert.Contains(received, line => line.Contains("warning 应触发"));
    }
}
