/**
 * 监听网卡枚举。
 * 职责：枚举本机可用于绑定 Web 服务监听的 IPv4 地址，供设置页选择以收敛暴露面。
 * 复用约定：纯 .NET 的 NetworkInterface API，无额外依赖；返回值首项固定为「全部网卡」哨兵
 *          （Address 为空串），与 AppSettings.WebSharingBindAddress 的空串语义一致。
 * 关键约束：只列处于 Up 状态且非回环的接口地址；枚举失败（网卡驱动异常等）时仅返回哨兵项，
 *          保证设置页始终可用，不因枚举问题挡住开关共享。
 */

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Pixbian.Services;

/// <summary>可绑定的监听地址选项。</summary>
/// <param name="DisplayName">展示名（网卡名 + 地址）。</param>
/// <param name="Address">绑定用 IPv4 地址；空串表示全部网卡。</param>
public sealed record WebBindOption(string DisplayName, string Address);

/// <summary>本机监听网卡枚举。</summary>
public static class NetworkInterfaceProvider
{
    /// <summary>「全部网卡」哨兵的展示名（含风险提示：默认选项暴露面最大）。</summary>
    private const string AllInterfacesDisplayName = "全部网卡（了解风险提示后使用）";

    /// <summary>枚举可绑定地址，首项为「全部网卡」哨兵。</summary>
    /// <returns>可供设置页下拉使用的选项列表；任何情况下至少含哨兵项。</returns>
    public static IReadOnlyList<WebBindOption> GetBindOptions()
    {
        var options = new List<WebBindOption> { new(AllInterfacesDisplayName, string.Empty) };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus is not OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily is not AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var address = unicast.Address.ToString();
                    options.Add(new WebBindOption($"{nic.Name} — {address}", address));
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // 枚举失败不阻断设置页：仅保留「全部网卡」项，行为与加固前一致。
        }

        return options;
    }
}
