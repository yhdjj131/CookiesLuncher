using System.Windows.Input;

namespace CookieLuncher.ViewModels;

/// <summary>列表中单个网站的展示模型。</summary>
public sealed class SiteItemViewModel : ObservableObject
{
    /// <summary>初始化条目。</summary>
    /// <param name="site">网站域名。</param>
    /// <param name="count">Cookie 数量。</param>
    /// <param name="launchCommand">启动命令（可为 null）。</param>
    public SiteItemViewModel(string site, int count, ICommand? launchCommand = null)
    {
        Site = site;
        Count = count;
        LaunchCommand = launchCommand;
    }

    /// <summary>网站域名。</summary>
    public string Site { get; }

    /// <summary>Cookie 数量。</summary>
    public int Count { get; }

    /// <summary>Cookie 数量的展示文案。</summary>
    public string CountText => $"{Count} 个 Cookie";

    /// <summary>启动命令。</summary>
    public ICommand? LaunchCommand { get; }
}
