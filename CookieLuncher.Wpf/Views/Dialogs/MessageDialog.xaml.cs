using System.Windows;

namespace CookieLuncher.Views.Dialogs;

/// <summary>统一的提示 / 确认对话框（风格与主界面一致）。</summary>
public partial class MessageDialog : Window
{
    private MessageDialog() => InitializeComponent();

    /// <summary>信息提示。</summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    public static void Info(Window? owner, string title, string message)
        => ShowCore(owner, title, message, confirm: false, "确定", null, danger: false);

    /// <summary>警告提示。</summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    public static void Warn(Window? owner, string title, string message)
        => ShowCore(owner, title, message, confirm: false, "确定", null, danger: false);

    /// <summary>错误提示。</summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    public static void Error(Window? owner, string title, string message)
        => ShowCore(owner, title, message, confirm: false, "确定", null, danger: true);

    /// <summary>是 / 否确认。</summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="confirmText">确认按钮文案。</param>
    /// <param name="cancelText">取消按钮文案。</param>
    /// <param name="danger">是否为破坏性操作（确认按钮用危险色）。</param>
    /// <returns>用户确认返回 true。</returns>
    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消",
        bool danger = false)
        => ShowCore(owner, title, message, confirm: true, confirmText, cancelText, danger);

    private static bool ShowCore(
        Window? owner,
        string title,
        string message,
        bool confirm,
        string confirmText,
        string? cancelText,
        bool danger)
    {
        var dialog = new MessageDialog
        {
            Owner = owner,
        };

        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;

        if (confirm)
        {
            dialog.CancelButton.Content = cancelText ?? "取消";
            dialog.CancelButton.Visibility = Visibility.Visible;
        }
        else
        {
            dialog.CancelButton.Visibility = Visibility.Collapsed;
        }

        if (danger)
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButton");
        }
        else
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("PrimaryButton");
        }

        return dialog.ShowDialog() == true;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
