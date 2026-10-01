using System.Diagnostics;
using System.Windows;
using CookieLuncher.Core;
using CookieLuncher.ViewModels;
using CookieLuncher.Views.Dialogs;

namespace CookieLuncher.Views;

/// <summary>用真实窗口实现 <see cref="IUserDialogs"/>。</summary>
public sealed class WpfDialogs : IUserDialogs
{
    /// <inheritdoc />
    public void Info(string title, string message) => MessageDialog.Info(Owner(), title, message);

    /// <inheritdoc />
    public void Warn(string title, string message) => MessageDialog.Warn(Owner(), title, message);

    /// <inheritdoc />
    public void Error(string title, string message) => MessageDialog.Error(Owner(), title, message);

    /// <inheritdoc />
    public bool Confirm(string title, string message, string confirmText = "确定", string cancelText = "取消")
        => MessageDialog.Confirm(Owner(), title, message, confirmText, cancelText);

    /// <inheritdoc />
    public string? PromptPassword(string title, string message, string label)
        => PasswordPromptDialog.PromptSingle(Owner(), title, message, label);

    /// <inheritdoc />
    public string? PromptNewPassword(string title, string message)
        => PasswordPromptDialog.PromptNew(Owner(), title, message);

    /// <inheritdoc />
    public PasteCookieInput? PromptPasteCookie(string? suggestedSite = null)
    {
        var dialog = new PasteCookieDialog(suggestedSite) { Owner = Owner() };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <inheritdoc />
    public CaptureCookieOutput? PromptCapture(AppSession session, string? suggestedSite = null)
    {
        var dialog = new CaptureCookieDialog(session, suggestedSite) { Owner = Owner() };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <inheritdoc />
    public bool PromptChangePassword(AppSession session)
    {
        var dialog = new ChangePasswordDialog(session) { Owner = Owner() };
        return dialog.ShowDialog() == true;
    }

    /// <inheritdoc />
    public bool PromptSettings(AppSession session)
    {
        var dialog = new SettingsDialog(session) { Owner = Owner() };
        return dialog.ShowDialog() == true;
    }

    /// <inheritdoc />
    public void OpenDirectory(string path)
    {
        try
        {
            // 允许传入文件路径：改为打开其所在目录，避免 CreateDirectory 因同名文件而失败
            var target = AppPaths.ToDirectoryPath(path);
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn("打开失败", $"无法打开目录 {path}：{ex.Message}");
        }
    }

    /// <summary>取当前活动窗口作为对话框父窗口。</summary>
    private static Window? Owner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        return app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive && window.IsVisible)
            ?? app.MainWindow;
    }
}
