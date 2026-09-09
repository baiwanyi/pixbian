/**
 * 当前网络位置类别检测。
 * 职责：判定本机是否存在「公用」网络，供局域网共享在启动前拒绝暴露媒体库。
 * 复用约定：经 COM 的 NetworkListManager 查询连接类别（0=公用、1=专用、2=域），
 *          属 Windows 专有能力，故本类只存在于界面层，领域与 Web 服务层保持平台无关。
 * 关键约束：检测失败一律按「可信」返回——取不到类别时误拒会让用户在自家网络里无法开启共享，
 *          误放行只是维持既有行为，二者权衡取后者；
 *          本类只做提示与闸门，不替代真正的加固（服务仍监听全部网卡，公用网络下的风险由拒绝启动规避）。
 */

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pixbian.Services;

/// <summary>网络位置类别检测。</summary>
[SupportedOSPlatform("windows")]
public static class NetworkCategoryDetector
{
    /// <summary>NetworkListManager 中「公用网络」的类别值。</summary>
    private const int PublicCategory = 0;

    /// <summary>判断当前是否存在公用网络连接。</summary>
    /// <returns>存在公用网络时为 true；无连接、无类别信息或查询失败时为 false。</returns>
    public static bool IsPublicNetwork()
    {
        try
        {
            var managerType = Type.GetTypeFromProgID(
                "NetworkListMgr.NetworkListManager",
                throwOnError: false);

            if (managerType is null)
            {
                return false;
            }

            dynamic manager = Activator.CreateInstance(managerType)!;

            foreach (var connection in manager.GetNetworkConnections())
            {
                // 未连接（含已断开的 Wi-Fi 配置）不参与判定，只考察当前生效的连接。
                if (!connection.IsConnected)
                {
                    continue;
                }

                if ((int)connection.GetNetwork().GetCategory() == PublicCategory)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is COMException
                                      or InvalidCastException
                                      or InvalidOperationException
                                      or NotSupportedException
                                      or MissingMethodException)
        {
            // 类别不可得时按可信处理，避免把用户挡在自家网络之外。
            return false;
        }
    }
}
