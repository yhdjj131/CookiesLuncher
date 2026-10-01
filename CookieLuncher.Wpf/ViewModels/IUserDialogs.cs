using System.Text.Json.Nodes;

namespace CookieLuncher.ViewModels;

/// <summary>粘贴导入对话框的输入结果。</summary>
/// <param name="Site">用户填写的域名或链接。</param>
/// <param name="CookieText">粘贴的 Cookie 文本。</param>
public readonly record struct PasteCookieInput(string Site, string CookieText);

/// <summary>浏览器采集的结果。</summary>
/// <param name="Site">目标网站域名。</param>
/// <param name="Cookies">采集到的 Cookie（已规范化）。</param>
public readonly record struct CaptureCookieOutput(string Site, List<JsonObject> Cookies);

/// <summary>
/// 视图模型层需要的所有交互入口。由 <c>WpfDialogs</c> 用真实窗口实现，
/// 使视图模型与具体窗口解耦。
/// </summary>
public interface IUserDialogs
{
    /// <summary>信息提示。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    void Info(string title, string message);

    /// <summary>警告提示。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    void Warn(string title, string message);

    /// <summary>错误提示。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    void Error(string title, string message);

    /// <summary>是 / 否确认。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="confirmText">确认按钮文案。</param>
    /// <param name="cancelText">取消按钮文案。</param>
    /// <returns>用户确认返回 true。</returns>
    bool Confirm(string title, string message, string confirmText = "确定", string cancelText = "取消");

    /// <summary>单字段密码输入。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">说明。</param>
    /// <param name="label">输入框标签。</param>
    /// <returns>输入的密码；取消返回 null。</returns>
    string? PromptPassword(string title, string message, string label);

    /// <summary>引导设置新密码（两次输入 + 长度校验）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">说明。</param>
    /// <returns>通过校验的新密码；取消返回 null。</returns>
    string? PromptNewPassword(string title, string message);

    /// <summary>粘贴导入 Cookie。</summary>
    /// <param name="suggestedSite">预填域名。</param>
    /// <returns>输入结果；取消返回 null。</returns>
    PasteCookieInput? PromptPasteCookie(string? suggestedSite = null);

    /// <summary>打开浏览器手动登录并采集 Cookie。</summary>
    /// <param name="session">运行期上下文。</param>
    /// <param name="suggestedSite">预填域名。</param>
    /// <returns>采集结果；取消返回 null。</returns>
    CaptureCookieOutput? PromptCapture(AppSession session, string? suggestedSite = null);

    /// <summary>修改访问密码。</summary>
    /// <param name="session">运行期上下文。</param>
    /// <returns>密码被成功修改返回 true。</returns>
    bool PromptChangePassword(AppSession session);

    /// <summary>编辑程序设置。</summary>
    /// <param name="session">运行期上下文。</param>
    /// <returns>设置有变更返回 true。</returns>
    bool PromptSettings(AppSession session);

    /// <summary>在资源管理器中打开目录。</summary>
    /// <param name="path">目录路径。</param>
    void OpenDirectory(string path);
}
