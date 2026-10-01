using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CookieLuncher.Core;

/// <summary>
/// 日志模块：按日期分文件、敏感信息脱敏、过期日志清理。
/// <para>
/// 日志文件：<c>logs/YYYY-MM-DD.log</c>；行格式：<c>2026-09-13 10:23:45 [INFO] 程序启动</c>。
/// 所有写入内容先经过脱敏：SESSDATA、bili_jct、token、session、auth、password、cookie、secret
/// 等字段的值保留前 6 位，其余替换为 <c>***</c>。
/// </para>
/// </summary>
public static class AppLogger
{
    /// <summary>需要脱敏的字段名（大小写不敏感）。</summary>
    private static readonly string[] SensitiveFields =
    [
        "sessdata",
        "bili_jct",
        "token",
        "session",
        "auth",
        "password",
        "cookie",
        "secret",
    ];

    /// <summary>匹配 "字段名 = 值" / "字段名: 值" / "\"字段名\": \"值\"" 等写法。</summary>
    private static readonly Regex SensitiveRegex = new(
        @"(?<key>\b(?:" + string.Join('|', SensitiveFields) + @")\b)(?<sep>""?\s*[=:]\s*""?)(?<value>[^\s;,&""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>日志文件名：YYYY-MM-DD.log。</summary>
    private static readonly Regex LogNameRegex = new(
        @"^(\d{4})-(\d{2})-(\d{2})\.log$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, int> LevelRanks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEBUG"] = 0,
        ["INFO"] = 1,
        ["WARNING"] = 2,
        ["ERROR"] = 3,
        ["CRITICAL"] = 4,
    };

    private static readonly object Gate = new();

    private static string _logDir = string.Empty;
    private static int _fileLevelRank = 1;
    private static string? _currentDate;
    private static StreamWriter? _writer;

    /// <summary>警告及以上级别的日志（供界面状态栏展示）。</summary>
    public static event Action<string>? WarningOrAbove;

    /// <summary>当前日志目录。</summary>
    public static string LogDirectory
    {
        get
        {
            lock (Gate)
            {
                return _logDir;
            }
        }
    }

    /// <summary>创建（或重建）日志写入器。</summary>
    /// <param name="logDir">日志目录。</param>
    /// <param name="level">写入文件的日志级别。</param>
    public static void Setup(string logDir, string level)
    {
        lock (Gate)
        {
            CloseWriter();
            _logDir = logDir;
            _fileLevelRank = Rank(level);
        }
    }

    /// <summary>关闭日志文件句柄。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            CloseWriter();
        }
    }

    /// <summary>记录 DEBUG 日志。</summary>
    /// <param name="message">日志内容。</param>
    public static void Debug(string message) => Write("DEBUG", message);

    /// <summary>记录 INFO 日志。</summary>
    /// <param name="message">日志内容。</param>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>记录 WARNING 日志。</summary>
    /// <param name="message">日志内容。</param>
    public static void Warning(string message) => Write("WARNING", message);

    /// <summary>记录 ERROR 日志。</summary>
    /// <param name="message">日志内容。</param>
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>记录 ERROR 日志（含异常堆栈）。</summary>
    /// <param name="message">日志内容。</param>
    /// <param name="exception">异常对象。</param>
    public static void Error(string message, Exception exception)
        => Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    /// <summary>对敏感值脱敏：保留前 <paramref name="keep"/> 位，其余替换为 <c>***</c>。</summary>
    /// <param name="value">原始值。</param>
    /// <param name="keep">保留的前缀长度。</param>
    /// <returns>脱敏后的字符串。</returns>
    /// <summary>对敏感值脱敏：保留前 <paramref name="keep"/> 位，其余用 <c>***</c> 代替。</summary>
    /// <param name="value">原始敏感值。</param>
    /// <param name="keep">保留的前缀长度。</param>
    /// <returns>脱敏后的字符串；长度不足时全部隐藏。</returns>
    public static string MaskValue(string? value, int keep = 6)
    {
        var text = value ?? string.Empty;
        return text.Length <= keep ? "***" : string.Concat(text.AsSpan(0, keep), "***");
    }

    /// <summary>对一整段文本按字段名做脱敏。</summary>
    /// <param name="text">原始文本。</param>
    /// <returns>脱敏后的文本。</returns>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            return SensitiveRegex.Replace(text, match =>
                match.Groups["key"].Value + match.Groups["sep"].Value + MaskValue(match.Groups["value"].Value));
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    /// <summary>删除超过保留天数的日志文件。</summary>
    /// <param name="retentionDays">保留天数（小于等于 0 时不清理）。</param>
    /// <returns>实际删除的文件数量。</returns>
    public static int CleanupOldLogs(int retentionDays)
    {
        var directory = LogDirectory;
        if (retentionDays <= 0 || string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var deadline = DateTime.Now - TimeSpan.FromDays(retentionDays);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.log"))
        {
            var match = LogNameRegex.Match(System.IO.Path.GetFileName(path));
            if (!match.Success)
            {
                continue;
            }

            if (!DateTime.TryParse(
                    $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var fileDate))
            {
                continue;
            }

            if (fileDate >= deadline)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warning($"删除过期日志失败 {path}: {ex.Message}");
            }
        }

        if (removed > 0)
        {
            Info($"已清理 {removed} 个过期日志文件（保留 {retentionDays} 天）");
        }

        return removed;
    }

    /// <summary>写入一行日志。</summary>
    private static void Write(string level, string message)
    {
        var line = Sanitize($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}");

        lock (Gate)
        {
            if (Rank(level) >= _fileLevelRank && _logDir.Length > 0)
            {
                try
                {
                    var date = DateTime.Now.ToString("yyyy-MM-dd");
                    if (_writer is null || _currentDate != date)
                    {
                        OpenWriter(date);
                    }

                    _writer?.WriteLine(line);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 日志写入失败不能影响主流程
                    CloseWriter();
                }
            }
        }

        if (Rank(level) >= LevelRanks["WARNING"])
        {
            try
            {
                WarningOrAbove?.Invoke(line);
            }
            catch (Exception)
            {
                // 订阅方异常不影响日志
            }
        }
    }

    private static void OpenWriter(string date)
    {
        CloseWriter();
        Directory.CreateDirectory(_logDir);
        var path = System.IO.Path.Combine(_logDir, date + ".log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n",
            AutoFlush = true,
        };
        _currentDate = date;
    }

    private static void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // 忽略关闭失败
        }

        _writer = null;
        _currentDate = null;
    }

    private static int Rank(string? level)
        => level is not null && LevelRanks.TryGetValue(level, out var rank) ? rank : LevelRanks["INFO"];
}
