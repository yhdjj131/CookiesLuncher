using System.Windows.Controls;
using CookieLuncher.ViewModels;

namespace CookieLuncher.Views.Pages;

/// <summary>「Cookie 管理」页面。</summary>
public partial class ManagePage : UserControl
{
    /// <summary>初始化页面。</summary>
    /// <param name="viewModel">视图模型。</param>
    public ManagePage(ManageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
