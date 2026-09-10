/**
 * 设置页代码后置——局域网访问（partial）。
 * 职责：共享开关、端口与密码的落盘、活跃会话的查看与踢出、访问地址经系统浏览器打开。
 * 复用约定：配置应用与状态经 SettingsViewModel.ApplyWebSharingAsync 完成（内部负责
 *          服务实例重建与广播）；会话踢出经 RevokeAllSessions / RevokeSessionById。
 * 关键约束：端口与上次应用值相同且未输入新密码时跳过应用——ApplyWebSharingAsync 会
 *          销毁并重建服务器实例，无谓重启会让已连接的客户端断开；密码只存哈希不回显，
 *          应用后必须立即清空输入框；访问地址来源为本机监听地址而非任意外部输入。
 */

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Pixbian.Views;

/// <summary>设置页的局域网访问。</summary>
public sealed partial class SettingsPage
{
    private bool _isWebSharingOn;

    /// <summary>上次已应用的端口文本；用于判断输入框失焦时是否真的需要重建服务。</summary>
    private string _appliedPortText = string.Empty;

    /// <summary>局域网开关是否打开；驱动「端口与密码」展开区的显隐与右侧状态文字。</summary>
    public bool IsWebSharingOn
    {
        get => _isWebSharingOn;
        set
        {
            if (_isWebSharingOn == value)
            {
                return;
            }

            _isWebSharingOn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WebSharingStateText));
        }
    }

    /// <summary>开关状态文字：置于开关左侧。</summary>
    public string WebSharingStateText => IsWebSharingOn ? "开启" : "关闭";

    /// <summary>按当前设置同步 Web 区块的控件状态。</summary>
    private void SyncWebSharingControls()
    {
        var settings = ViewModel.Settings;

        WebSharingToggle.IsOn = settings.IsWebSharingEnabled;
        IsWebSharingOn = settings.IsWebSharingEnabled;
        WebPortBox.Text = settings.WebSharingPort.ToString(CultureInfo.InvariantCulture);
        _appliedPortText = WebPortBox.Text;

        // 网卡选项每次进入设置页重枚举：Wi-Fi 与有线之间切换后地址集合会变。
        // 回填期间抑制 SelectionChanged 的应用逻辑，避免仅打开设置页就重建一次服务。
        _isSyncingWebBind = true;
        try
        {
            ViewModel.RefreshWebBindOptions();
        }
        finally
        {
            _isSyncingWebBind = false;
        }
    }

    /// <summary>回填网卡下拉期间为 true：此时的选择变化来自程序而非用户，不得触发服务重建。</summary>
    private bool _isSyncingWebBind;

    /// <summary>监听网卡切换：落盘并重建服务，使暴露面收敛即时生效。</summary>
    private async void OnWebBindSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingWebBind)
        {
            return;
        }

        await ApplyWebSharingAsync();
    }

    /// <summary>开关切换即生效，并驱动「端口与密码」展开区的显隐。</summary>
    private async void OnWebSharingToggled(object sender, RoutedEventArgs e)
    {
        IsWebSharingOn = WebSharingToggle.IsOn;
        await ApplyWebSharingAsync();
    }

    /// <summary>端口或密码输入框失焦时应用配置，替代「保存」按钮。</summary>
    /// <remarks>
    /// 端口与上次应用值相同且未输入新密码时直接跳过：ApplyWebSharingAsync 会销毁并重建
    /// 服务器实例，无谓重启会让已连接的局域网客户端断开。
    /// </remarks>
    private async void OnWebSettingLostFocus(object sender, RoutedEventArgs e)
    {
        var portChanged = !string.Equals(WebPortBox.Text, _appliedPortText, StringComparison.Ordinal);

        if (!portChanged && WebPasswordBox.Password.Length == 0)
        {
            return;
        }

        await ApplyWebSharingAsync();
    }

    /// <summary>按控件当前值应用局域网配置。</summary>
    private async Task ApplyWebSharingAsync()
    {
        var port = int.TryParse(WebPortBox.Text, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 8756;

        await ViewModel.ApplyWebSharingAsync(
            WebSharingToggle.IsOn,
            port,
            WebPasswordBox.Password,
            ViewModel.SelectedWebBindAddress);

        // 密码只存哈希、不回显明文，应用后立即清空输入框。
        WebPasswordBox.Password = string.Empty;
        _appliedPortText = WebPortBox.Text;
        OnPropertyChanged(nameof(ViewModel.WebStatusText));
    }

    /// <summary>踢出全部活跃会话：所有已登录设备需重新登录。</summary>
    private void OnRevokeSessionsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RevokeAllSessions();
    }

    /// <summary>踢出单个活跃会话：会话的公开 ID 经按钮 Tag 传入（非认证令牌）。</summary>
    private void OnRevokeSessionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sessionId })
        {
            ViewModel.RevokeSessionById(sessionId);
        }
    }

    /// <summary>点击访问地址：交给系统默认浏览器打开。</summary>
    /// <remarks>
    /// 显式走 ShellExecute（UseShellExecute 单参数、无命令拼接），不依赖
    /// HyperlinkButton.NavigateUri 的桌面自动行为；URL 先经绝对 URI 校验，
    /// 且来源为本机 Web 服务自身的监听地址，非任意外部输入。
    /// </remarks>
    private void OnWebAccessUrlClick(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { Tag: string url }
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }
}
