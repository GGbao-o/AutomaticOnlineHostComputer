using AutomaticOnlineHostComputer.Views.Controller;
using AutomaticOnlineHostComputer.Views.Crane;
using AutomaticOnlineHostComputer.Views.FlowStatus;
using AutomaticOnlineHostComputer.Views.Home;
using AutomaticOnlineHostComputer.Views.Machine;
using AutomaticOnlineHostComputer.Views.Motion;
using AutomaticOnlineHostComputer.Views.Overview;
using AutomaticOnlineHostComputer.Views.Process;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Infrastructure.Navigation;

/*
 * ╔══════════════════════════════════════════════════════════════════════════╗
 * ║                    CachedNavigationService（页面缓存导航）               ║
 * ╠══════════════════════════════════════════════════════════════════════════╣
 * ║  背景问题：                                                              ║
 * ║  之前 MainWindow 菜单每次点击都会 new XxxView()：                       ║
 * ║    1) 切换回来后页面状态丢失（选中行、滚动位置、输入内容）               ║
 * ║    2) Loaded 事件重复触发，导致数据库重复查询                            ║
 * ║                                                                          ║
 * ║  解决方案：                                                              ║
 * ║  引入“按菜单键缓存页面实例”的导航服务：                                 ║
 * ║    • 首次导航到某页：创建实例并缓存                                      ║
 * ║    • 再次导航到同页：直接返回缓存实例                                    ║
 * ║                                                                          ║
 * ║  效果：                                                                  ║
 * ║    • 页面状态保持                                                        ║
 * ║    • 避免不必要的重复查询                                                ║
 * ║    • 切换更流畅                                                          ║
 * ╚══════════════════════════════════════════════════════════════════════════╝
 */

/// <summary>
/// 提供基于菜单 Tag 的页面导航能力，并对页面实例做内存缓存。
/// </summary>
public sealed class CachedNavigationService
{
    /// <summary>
    /// 页面缓存：key = 菜单 Tag（main/crane/machine/controller/process/motion/overview/flowStatus），
    /// value = 对应的 UserControl 实例。
    /// </summary>
    private readonly Dictionary<string, UserControl> _pageCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 根据菜单 Tag 获取页面实例（有缓存则复用，无缓存则创建并加入缓存）。
    /// </summary>
    public UserControl GetOrCreatePage(string? menuTag)
    {
        
        Console.WriteLine($"CachedNavigationService: 请求页面 '{menuTag}'...");
        var key = string.IsNullOrWhiteSpace(menuTag) ? "main" : menuTag;
        //检查缓存中是否已有该页面实例。如果有，直接返回，避免重复创建
        if (_pageCache.TryGetValue(key, out var cachedPage))
            return cachedPage;

        // 显式声明为 UserControl，避免 switch 分支在编译期无法推断公共最佳类型（CS8506）。
        UserControl page = key switch
        {
            "main" => new HomeView(),
            "crane" => new CraneManagementView(),
            "machine" => new MachineManagementView(),
            "controller" => new ControllerManagementView(),
            "process" => new ProcessManagementView(),
            "motion" => new MotionSettingsView(),
            "overview" => new EquipmentOverviewView(),
            "flowStatus" => new FlowStatusView(),
            _ => new HomeView()
        };

        _pageCache[key] = page;
        return page;
    }
}
