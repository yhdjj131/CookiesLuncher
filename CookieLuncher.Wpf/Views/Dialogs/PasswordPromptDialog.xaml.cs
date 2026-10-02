using System.Windows;
using CookieLuncher.Core;

namespace CookieLuncher.Views.Dialogs;

/// <summary>密码输入对话框：支持「单次输入」与「新密码 + 确认」两种模式。</summary>
public partial class PasswordPromptDialog : Window
{
    private bool _requireConfirm;
    private int _minLength = PasswordRules.MinLength;

    private PasswordPromptDialog() => InitializeComponent();

    /// <summary>输入结果。</summary>
    public string Result { get; private set; } = string.Empty;

    /// <summary>单次密码输入（非空校验）。</summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">说明。</param>
    /// <param name="label">输入框标签。</param>
    /// <returns>输入的密码；取消返回 null。</returns>
    public static string? PromptSingle(Window? owner, string title, string message, string label)
    {
        var dialog = Create(owner, title, message, label);
        dialog._requireConfirm = false;
        dialog.ConfirmGroup.Visibility = Visibility.Collapsed;
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>设置新密码（长度、首尾空格、两次一致校验）。</summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">说明。</param>
    /// <param name="label">第一个输入框标签。</param>
    /// <param name="confirmLabel">第二个输入框标签。</param>
    /// <param name="minLength">最小长度。</param>
    /// <returns>通过校验的密码；取消返回 null。</returns>
    public static string? PromptNew(
        Window? owner,
        string title,
        string message,
        string label = "新密码",
        string confirmLabel = "再次输入新密码",
        int minLength = PasswordRules.MinLength)
    {
        var dialog = Create(owner, title, message, label);
        dialog._requireConfirm = true;
        dialog._minLength = minLength;
        dialog.ConfirmGroup.Visibility = Visibility.Visible;
        dialog.ConfirmLabelText.Text = confirmLabel;
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private static PasswordPromptDialog Create(Window? owner, string title, string message, string label)
    {
        var dialog = new PasswordPromptDialog
        {
            Owner = owner,
        };

        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.LabelText.Text = label;
        dialog.Loaded += (_, _) => dialog.PasswordInput.Focus();
        return dialog;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var password = PasswordInput.Password;
        var confirm = ConfirmInput.Password;

        var error = _requireConfirm
            ? PasswordRules.ValidateNew(password, confirm, _minLength)
            : PasswordRules.ValidateNonEmpty(password);

        if (error is not null)
        {
            ShowError(error);
            return;
        }

        Result = password;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PasswordInput.SelectAll();
        PasswordInput.Focus();
    }
}
