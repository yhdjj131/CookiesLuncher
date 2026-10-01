using System.Diagnostics;
using System.Management;

namespace CookieLuncher.Core;

/// <summary>进程快照信息（仅本程序内部使用）。</summary>
/// <param name="Pid">进程 ID。</param>
/// <param name="ParentPid">父进程 ID。</param>
/// <param name="Name">进程名。</param>
/// <param name="CommandLine">命令行（可能为空：部分受保护进程不允许读取）。</param>
internal readonly record struct ProcessInfo(int Pid, int ParentPid, string Name, string CommandLine);

/// <summary>
/// 浏览器进程相关的辅助操作：进程树等待、按命令行查杀残留进程、临时目录清理。
/// <para>等价于 Python 版依赖 psutil 的实现（此处通过 WMI 的 Win32_Process 获取同样的信息）。</para>
/// </summary>
public static class ProcessUtil
{
    /// <summary>等待进程树退出的轮询间隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 获取全部进程的快照。WMI 不可用时返回空列表（调用方需容忍）。
    /// </summary>
    /// <returns>进程快照列表。</returns>
    private static List<ProcessInfo> Snapshot()
    {
        var result = new List<ProcessInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process");
            using var collection = searcher.Get();
            foreach (var item in collection)
            {
                using (item)
                {
                    try
                    {
                        result.Add(new ProcessInfo(
                            ToInt(item["ProcessId"]),
                            ToInt(item["ParentProcessId"]),
                            item["Name"] as string ?? string.Empty,
                            item["CommandLine"] as string ?? string.Empty));
                    }
                    catch (ManagementException)
                    {
                        // 单条记录读取失败不影响整体
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // WMI 不可用：返回空快照，交由调用方降级处理
        }

        return result;
    }

    /// <summary>
    /// 监控主 PID 及全部递归子进程，等待整套进程树退出。
    /// </summary>
    /// <param name="pid">根进程 ID。</param>
    /// <param name="timeout">总等待超时。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>进程树已全部退出返回 true；超时（并已尝试终止根进程）返回 false。</returns>
    public static bool WaitProcessTreeExit(int pid, TimeSpan timeout, Action<string>? log = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = Snapshot();

            // 根进程已退出（等价 psutil.NoSuchProcess）直接返回
            if (snapshot.All(process => process.Pid != pid))
            {
                return true;
            }

            if (Descendants(snapshot, pid).Count == 0)
            {
                return true;
            }

            Thread.Sleep(PollInterval);
        }

        log?.Invoke($"等待 {timeout.TotalSeconds:F0} 秒后浏览器进程树仍未完全退出，尝试终止");
        TryTerminate(pid);
        return false;
    }

    /// <summary>
    /// 按临时用户数据目录匹配并查杀残留浏览器进程（thorium / system 通用）。
    /// <para>仅清理命令行中包含本次临时目录的进程，不会误伤用户自行打开的其它浏览器实例。</para>
    /// </summary>
    /// <param name="userDataDir">本次会话使用的临时用户数据目录。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="waitTimeout">温和终止的等待上限。</param>
    public static void KillProcessesUsingPath(string userDataDir, Action<string>? log = null, TimeSpan? waitTimeout = null)
    {
        var target = userDataDir.ToLowerInvariant();
        var deadline = DateTime.UtcNow + (waitTimeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            var alive = Matching(snapshot: Snapshot(), target);
            if (alive.Count == 0)
            {
                return;
            }

            foreach (var process in alive)
            {
                TryTerminate(process.Pid);
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(300));
        }

        // 超时仍未退出，升级为强杀
        foreach (var process in Matching(Snapshot(), target))
        {
            TryKill(process.Pid);
        }

        log?.Invoke("残留浏览器进程未完全退出，已强制终止");
    }

    /// <summary>删除目录，失败重试直到超时（文件占用通常因进程未完全退出）。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="timeout">重试超时。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>目录最终不存在返回 true。</returns>
    public static bool RemoveDirectoryWithRetry(string path, TimeSpan timeout, Action<string>? log = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                log?.Invoke("临时目录已清理");
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    var removed = TryForceRemove(path);
                    if (Directory.Exists(path))
                    {
                        log?.Invoke($"临时目录删除失败（可能仍被进程占用）: {path}");
                        return false;
                    }

                    log?.Invoke("临时目录已清理");
                    return removed;
                }

                Thread.Sleep(PollInterval);
            }
        }
    }

    /// <summary>清理程序异常退出残留的临时目录。</summary>
    /// <param name="prefix">目录名前缀。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>清理的目录数量。</returns>
    public static int CleanupStaleTempDirs(string prefix, Action<string>? log = null)
    {
        var removed = 0;
        try
        {
            var tempRoot = Path.GetTempPath();
            var directories = Directory.EnumerateDirectories(tempRoot, prefix + "*").ToList();
            if (directories.Count == 0)
            {
                return 0;
            }

            // 只清理确实没人使用的目录：命令行里带该目录的存活进程（浏览器）说明它正在被使用，
            // 例如另一份程序（控制台版或本程序的另一个实例）正在运行。
            var snapshot = Snapshot();

            foreach (var directory in directories)
            {
                if (IsReferencedByLiveProcess(snapshot, directory))
                {
                    log?.Invoke($"跳过正在使用的临时目录: {directory}");
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                    log?.Invoke($"已清理遗留临时目录: {directory}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"遗留临时目录清理失败（可能被占用）: {directory}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"扫描临时目录失败: {ex.Message}");
        }

        return removed;
    }

    /// <summary>判断指定目录是否正被某个存活进程使用（命令行中包含该目录）。</summary>
    private static bool IsReferencedByLiveProcess(List<ProcessInfo> snapshot, string directory)
        => Matching(snapshot, directory.ToLowerInvariant()).Count > 0;

    private static List<ProcessInfo> Descendants(List<ProcessInfo> snapshot, int rootPid)
    {
        var alive = snapshot.ToDictionary(process => process.Pid);
        var children = new Dictionary<int, List<int>>();
        foreach (var process in snapshot)
        {
            if (!children.TryGetValue(process.ParentPid, out var siblings))
            {
                siblings = [];
                children[process.ParentPid] = siblings;
            }

            siblings.Add(process.Pid);
        }

        var result = new List<ProcessInfo>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        var visited = new HashSet<int> { rootPid };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!children.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var childPid in next)
            {
                if (visited.Add(childPid) && alive.TryGetValue(childPid, out var child))
                {
                    result.Add(child);
                    queue.Enqueue(childPid);
                }
            }
        }

        return result;
    }

    private static List<ProcessInfo> Matching(List<ProcessInfo> snapshot, string lowerCaseTarget)
        => snapshot
            .Where(process => process.CommandLine.Length > 0
                && process.CommandLine.Contains(lowerCaseTarget, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static void TryTerminate(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程已退出或无权操作
        }
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程已退出或无权操作
        }
    }

    private static bool TryForceRemove(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static int ToInt(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToInt32(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }
}
