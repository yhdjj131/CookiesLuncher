using System.Windows;
using CookieLuncher.Core;
using CookieLuncher.ViewModels;

namespace CookieLuncher.Views;

/// <summary>解锁 / 首次设置访问密码窗口。</summary>
public partial class AuthWindow : Window
{
    private readonly AppConfig _config;
    private readonly AuthViewModel _viewModel;

    /// <summary>初始化窗口。</summary>
    /// <param name="config">配置对象。</param>
    public AuthWindow(AppConfig config)
    {
        InitializeComponent();

        _config = config;
        _viewModel = new AuthViewModel(config);
        DataContext = _viewModel;

        ConfirmGroup.Visibility = _viewModel.IsSetup ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>解锁成功后得到的 Fernet 密钥。</summary>
    public FernetKey? Key { get; private set; }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.ClearError();

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryUnlock(_config, PasswordInput.Password, ConfirmInput.Password, out var key))
        {
            Key = key;
            DialogResult = true;
            return;
        }

        if (_viewModel.IsSetup)
        {
            ConfirmInput.Password = string.Empty;
            ConfirmInput.Focus();
        }
        else
        {
            PasswordInput.SelectAll();
            PasswordInput.Focus();
        }
    }
}
