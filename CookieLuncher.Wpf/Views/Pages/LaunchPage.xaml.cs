using System.Windows.Controls;
using CookieLuncher.ViewModels;

namespace CookieLuncher.Views.Pages;

/// <summary>「启动浏览器」页面。</summary>
public partial class LaunchPage : UserControl
{
    /// <summary>初始化页面。</summary>
    /// <param name="viewModel">视图模型。</param>
    public LaunchPage(LaunchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
