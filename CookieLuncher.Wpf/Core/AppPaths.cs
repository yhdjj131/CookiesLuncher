namespace CookieLuncher.Core;

/// <summary>
/// 统一解析程序数据目录与各类文件路径。
/// <para>
/// 解析顺序（先命中者生效）：
/// <list type="number">
///   <item>环境变量 <c>COOKIELUNCHER_ROOT</c>；</item>
///   <item>命令行参数 <c>--root &lt;目录&gt;</c>；</item>
///   <item>从可执行文件目录逐级向上查找 Python 版源码布局（含 <c>core/crypto.py</c> 的目录），
///         便于在 <c>bin/</c> 下调试时直接复用仓库根目录的 config.json 与 cookies.enc；</item>
///   <item>可执行文件所在目录（发布后的默认行为）。</item>
/// </list>
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>覆盖数据目录的环境变量名。</summary>
    public const string RootEnvVar = "COOKIELUNCHER_ROOT";

    /// <summary>向上查找仓库根目录时使用的标记文件。</summary>
    private const string RepoMarkerDirectory = "core";
    private const string RepoMarkerFile = "crypto.py";

    /// <summary>向上查找的最大层数。</summary>
    private const int MaxUpLevels = 6;

    private static string? _root;

    /// <summary>程序数据目录（config.json / cookies.enc / logs 都位于此处）。</summary>
    public static string Root => _root ??= Resolve(null);

    /// <summary>config.json 路径。</summary>
    public static string ConfigPath => Path.Combine(Root, "config.json");

    /// <summary>cookies.enc 路径。</summary>
    public static string CookiePath => Path.Combine(Root, "cookies.enc");

    /// <summary>导出产物路径（文件名取自 <see cref="Exporter.OutputFileName"/>，避免两处重复定义）。</summary>
    public static string ExportPath => Path.Combine(Root, Exporter.OutputFileName);

    /// <summary>日志目录。</summary>
    public static string LogDir => Path.Combine(Root, "logs");

    /// <summary>内置浏览器根目录。</summary>
    public static string ExternalBrowsersDir => Path.Combine(Root, "external_browsers");

    /// <summary>内置 Thorium 可执行文件路径。</summary>
    public static string ThoriumExePath =>
        Path.Combine(ExternalBrowsersDir, "thorium-win64", "thorium.exe");

    /// <summary>
    /// 把可能指向文件的路径转换为所在目录路径。
    /// <para>用于「打开数据目录」这类操作：即使传入 config.json 这类文件路径，也能正确打开其所在目录。</para>
    /// </summary>
    /// <param name="path">文件或目录路径。</param>
    /// <returns>目录路径；路径不存在且不像文件路径时原样返回。</returns>
    public static string ToDirectoryPath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        return Path.GetDirectoryName(full) ?? full;
    }

    /// <summary>
    /// 解析并固定数据目录。必须在访问 <see cref="Root"/> 之前调用一次。
    /// </summary>
    /// <param name="commandLineRoot">命令行传入的数据目录（可为 null）。</param>
    /// <exception cref="ConfigException">显式指定的数据目录不可用时抛出。</exception>
    public static void Initialize(string? commandLineRoot)
    {
        _root = Resolve(commandLineRoot);
    }

    /// <summary>
    /// 从命令行参数中解析 <c>--root &lt;目录&gt;</c> / <c>--root=&lt;目录&gt;</c>。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>解析出的目录；未提供时返回 null。</returns>
    public static string? ParseRootArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--root=".Length..].Trim('"');
                if (value.Length > 0)
                {
                    return value;
                }
            }
            else if (arg.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// 按既定优先级解析数据目录。
    /// </summary>
    /// <param name="commandLineRoot">命令行传入的数据目录（可为 null）。</param>
    /// <returns>绝对路径形式的数据目录。</returns>
    /// <exception cref="ConfigException">显式指定的数据目录不可用时抛出。</exception>
    public static string Resolve(string? commandLineRoot)
    {
        var requested = Environment.GetEnvironmentVariable(RootEnvVar);
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = commandLineRoot;
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            // 显式指定的数据目录：不存在时直接创建，绝不静默改用别的目录
            try
            {
                var full = Path.GetFullPath(requested);
                Directory.CreateDirectory(full);
                return full;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                or NotSupportedException)
            {
                throw new ConfigException(
                    $"无法使用指定的数据目录「{requested}」: {ex.Message}"
                    + $"（去掉 --root / {RootEnvVar} 可改用默认目录）", ex);
            }
        }

        // 未显式指定：从可执行文件目录逐级向上查找 Python 版源码布局，
        // 便于在 bin/ 下调试时直接复用仓库根目录的 config.json 与 cookies.enc
        var baseDirectory = AppContext.BaseDirectory;
        var directory = new DirectoryInfo(baseDirectory);
        for (var level = 0; level < MaxUpLevels && directory is not null; level++, directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, RepoMarkerDirectory, RepoMarkerFile);
            if (File.Exists(marker))
            {
                return directory.FullName;
            }
        }

        return Path.GetFullPath(baseDirectory);
    }
}
