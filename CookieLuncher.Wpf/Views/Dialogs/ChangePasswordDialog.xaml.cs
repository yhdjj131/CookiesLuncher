using System.Windows;
using CookieLuncher.Core;
using CookieLuncher.ViewModels;

namespace CookieLuncher.Views.Dialogs;

/// <summary>修改访问密码对话框。</summary>
public partial class ChangePasswordDialog : Window
{
    private readonly AppSession _session;

    /// <summary>初始化对话框。</summary>
    /// <param name="session">运行期上下文。</param>
    public ChangePasswordDialog(AppSession session)
    {
        InitializeComponent();
        _session = session;
        Loaded += (_, _) => OldInput.Focus();
    }

    private void OnInputChanged(object sender, RoutedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var oldPassword = OldInput.Password;
        var newPassword = NewInput.Password;

        if (PasswordRules.ValidateNonEmpty(oldPassword) is { } oldError)
        {
            ShowError(oldError);
            return;
        }

        if (PasswordRules.ValidateNew(newPassword, ConfirmInput.Password) is { } newError)
        {
            ShowError(newError);
            return;
        }

        if (newPassword == oldPassword)
        {
            ShowError("新密码与旧密码相同，无需修改。");
            return;
        }

        try
        {
            _session.ChangePassword(oldPassword, newPassword);
            AppLogger.Info("密码修改成功");
            DialogResult = true;
        }
        catch (PasswordChangeException ex)
        {
            ShowError(ex.Message);
            AppLogger.Warning($"修改密码失败: {ex.Message}");
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
