using System.Windows;
using CookieLuncher.Core;
using CookieLuncher.ViewModels;

namespace CookieLuncher.Views.Dialogs;

/// <summary>粘贴导入 Cookie 对话框。</summary>
public partial class PasteCookieDialog : Window
{
    /// <summary>初始化对话框。</summary>
    /// <param name="suggestedSite">预填域名。</param>
    public PasteCookieDialog(string? suggestedSite = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(suggestedSite))
        {
            SiteInput.Text = suggestedSite;
        }

        Loaded += (_, _) => CookieInput.Focus();
    }

    /// <summary>导入结果。</summary>
    public PasteCookieInput Result { get; private set; }

    private void OnCookieTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdatePreview();

    /// <summary>根据当前输入给出解析预览（条数 + 名称），不显示 Cookie 值。</summary>
    private void UpdatePreview()
    {
        var rawSite = SiteInput.Text.Trim();
        var rawCookie = CookieInput.Text;

        if (rawCookie.Trim().Length == 0)
        {
            PreviewText.Text = string.Empty;
            PreviewText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextSecondary");
            return;
        }

        try
        {
            var site = CookieParser.NormalizeSite(rawSite.Length > 0 ? rawSite : "example.com");
            var (cookies, warnings) = CookieParser.ParseDetailed(rawCookie, site);
            PreviewText.Text = $"解析成功：{cookies.Count} 条 Cookie（{CookieParser.Summarize(cookies)}）"
                + (warnings.Count > 0 ? $"；{warnings.Count} 条提示" : string.Empty);
            PreviewText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextSecondary");
        }
        catch (CookieParseException ex)
        {
            PreviewText.Text = ex.Message;
            PreviewText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var rawSite = SiteInput.Text.Trim();
        if (rawSite.Length == 0)
        {
            PreviewText.Text = "请填写网站域名。";
            PreviewText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
            SiteInput.Focus();
            return;
        }

        try
        {
            var site = CookieParser.NormalizeSite(rawSite);
            CookieParser.ParseDetailed(CookieInput.Text, site);
            Result = new PasteCookieInput(site, CookieInput.Text);
            DialogResult = true;
        }
        catch (CookieParseException ex)
        {
            PreviewText.Text = ex.Message;
            PreviewText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
