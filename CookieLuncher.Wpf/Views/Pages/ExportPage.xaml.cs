using System.Windows.Controls;
using CookieLuncher.ViewModels;

namespace CookieLuncher.Views.Pages;

/// <summary>「导出配置」页面。</summary>
public partial class ExportPage : UserControl
{
    /// <summary>初始化页面。</summary>
    /// <param name="viewModel">视图模型。</param>
    public ExportPage(ExportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
