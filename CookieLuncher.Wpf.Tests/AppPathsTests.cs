using CookieLuncher.Core;

namespace CookieLuncher.Tests;

/// <summary>数据目录解析测试。</summary>
public sealed class AppPathsTests : TempRootTest
{
    [Fact]
    public void Resolve_ShouldCreateExplicitlyRequestedDirectory()
    {
        var target = Path.Combine(Root, "nested", "data");
        Assert.False(Directory.Exists(target));

        var resolved = AppPaths.Resolve(target);

        Assert.Equal(Path.GetFullPath(target), resolved);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Resolve_ShouldPreferEnvironmentVariableOverCommandLine()
    {
        var fromEnvironment = Path.Combine(Root, "env-root");
        var fromCommandLine = Path.Combine(Root, "cli-root");
        Directory.CreateDirectory(fromEnvironment);
        Directory.CreateDirectory(fromCommandLine);

        var previous = Environment.GetEnvironmentVariable(AppPaths.RootEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.RootEnvVar, fromEnvironment);
        try
        {
            Assert.Equal(Path.GetFullPath(fromEnvironment), AppPaths.Resolve(fromCommandLine));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.RootEnvVar, previous);
        }

        // 环境变量为空时回落到命令行参数
        Assert.Equal(Path.GetFullPath(fromCommandLine), AppPaths.Resolve(fromCommandLine));
    }

    [Theory]
    [InlineData(new[] { "--root", "D:\\CookieData" }, "D:\\CookieData")]
    [InlineData(new[] { "--root=D:\\CookieData" }, "D:\\CookieData")]
    [InlineData(new[] { "--ROOT", "D:\\CookieData" }, "D:\\CookieData")]
    [InlineData(new[] { "other", "--root", "D:\\CookieData" }, "D:\\CookieData")]
    [InlineData(new[] { "--root" }, null)]
    [InlineData(new string[0], null)]
    public void ParseRootArgument_ShouldSupportBothForms(string[] args, string? expected)
        => Assert.Equal(expected, AppPaths.ParseRootArgument(args));

    [Fact]
    public void DataFilePaths_ShouldStayInsideRoot()
        => Assert.Matches(
            @"^" + System.Text.RegularExpressions.Regex.Escape(Path.GetFullPath(Root)) + @"\\",
            Path.GetFullPath(AppPaths.CookiePath));

    [Fact]
    public void ToDirectoryPath_ShouldRedirectFileToItsDirectory()
    {
        // 「打开数据目录」按钮曾传入 config.json 这类文件路径，
        // 直接 CreateDirectory 会抛 IOException，因此必须转换为所在目录
        var file = Path.Combine(Root, "config.json");
        File.WriteAllText(file, "{}");

        Assert.Equal(Path.GetFullPath(Root), AppPaths.ToDirectoryPath(file));
        Assert.Equal(Path.GetFullPath(Root), AppPaths.ToDirectoryPath(Root));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Root, "new-folder")),
            AppPaths.ToDirectoryPath(Path.Combine(Root, "new-folder")));
    }

    [Fact]
    public void ExportPath_ShouldUseExporterFileName()
        => Assert.Equal(
            Path.Combine(Root, Exporter.OutputFileName),
            AppPaths.ExportPath);
}
