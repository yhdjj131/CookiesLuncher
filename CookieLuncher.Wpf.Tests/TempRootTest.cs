using CookieLuncher.Core;

// 多个测试类都会操作 AppPaths 等静态状态，禁用并行执行以保证隔离。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CookieLuncher.Tests;

/// <summary>测试辅助：把数据目录指向临时目录。</summary>
public abstract class TempRootTest : IDisposable
{
    private readonly string _previousRoot;

    /// <summary>初始化临时数据目录。</summary>
    protected TempRootTest()
    {
        Root = Path.Combine(Path.GetTempPath(), "cookieluncher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        _previousRoot = AppPaths.Root;
        AppPaths.Initialize(Root);
    }

    /// <summary>本次测试使用的临时数据目录。</summary>
    protected string Root { get; }

    /// <summary>清理临时目录并恢复数据目录设置。</summary>
    public void Dispose()
    {
        AppLogger.Shutdown();
        AppPaths.Initialize(_previousRoot);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 句柄尚未释放时重试，仍失败则忽略（临时目录由系统清理）
                Thread.Sleep(50);
            }
        }

        GC.SuppressFinalize(this);
    }
}
