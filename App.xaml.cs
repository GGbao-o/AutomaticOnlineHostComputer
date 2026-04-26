using AutomaticOnlineHostComputer.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace AutomaticOnlineHostComputer;

/*
 * ╔══════════════════════════════════════════════════════════════════════════╗
 * ║                           App.xaml.cs                                    ║
 * ╠══════════════════════════════════════════════════════════════════════════╣
 * ║  WPF 应用程序入口。这里是 DI 容器的"启动点"：                             ║
 * ║                                                                          ║
 * ║  OnStartup 执行顺序：                                                    ║
 * ║    1. new ServiceCollection()   —— 创建服务注册表（空容器蓝图）           ║
 * ║    2. AddApplicationServices()  —— 向注册表里登记所有服务的创建规则       ║
 * ║    3. BuildServiceProvider()    —— 根据注册表构建真正的容器（工厂）       ║
 * ║    4. 保存到静态属性 Services   —— 供全局任何地方解析服务                 ║
 * ║                                                                          ║
 * ║  为什么用静态属性而不是构造函数注入？                                      ║
 * ║  WPF 的 UserControl/Window 由 XAML 解析器直接实例化（无参构造），         ║
 * ║  无法像 ASP.NET Core Controller 那样自动注入构造函数参数。                ║
 * ║  因此采用"服务定位器（Service Locator）"模式：                            ║
 * ║    App.Services.GetRequiredService<ManagementQueryService>()             ║
 * ║  这是 WPF 官方推荐的简单 DI 集成方式。                                   ║
 * ╚══════════════════════════════════════════════════════════════════════════╝
 */

public partial class App : Application
{
    /// <summary>
    /// 全局 DI 容器，在 OnStartup 中初始化，程序生命周期内只读。
    /// 使用 GetRequiredService&lt;T&gt;() 解析服务；若服务未注册会抛出异常，
    /// 比 GetService&lt;T&gt;() 返回 null 更容易发现配置遗漏。
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// 程序启动时执行（先于 MainWindow 构造）。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 构建 DI 容器 ──────────────────────────────────────────────────
        var serviceCollection = new ServiceCollection();

        // 调用扩展方法，集中注册所有业务服务
        serviceCollection.AddApplicationServices();

        // BuildServiceProvider() 冻结注册表，返回可解析服务的容器
        Services = serviceCollection.BuildServiceProvider();
    }
}
