using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Navigation;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer.Infrastructure.DependencyInjection;

/*
 * ╔══════════════════════════════════════════════════════════════════════════╗
 * ║              ServiceCollectionExtensions — DI 服务注册扩展               ║
 * ╠══════════════════════════════════════════════════════════════════════════╣
 * ║  什么是 DI（依赖注入）？                                                  ║
 * ║  传统写法：哪里用到服务，就在哪里 new，对象的创建分散在各处，难以维护。     ║
 * ║                                                                          ║
 * ║  DI 写法：                                                               ║
 * ║    1. 在"注册阶段"（程序启动时）统一声明"我要用哪些服务、怎么创建"。       ║
 * ║    2. 在"使用阶段"（View/ViewModel 构造时）从容器"解析"服务，              ║
 * ║       容器自动 new 对象并管理生命周期。                                    ║
 * ║                                                                          ║
 * ║  三种生命周期：                                                           ║
 * ║    Singleton  —— 整个程序只创建一个实例（适合无状态、重量级的服务）        ║
 * ║    Scoped     —— 每个"作用域"一个实例（WPF 通常不用）                     ║
 * ║    Transient  —— 每次解析都创建新实例（适合轻量、有状态的服务）            ║
 * ╚══════════════════════════════════════════════════════════════════════════╝
 */

/// <summary>
/// 封装所有服务注册逻辑的扩展方法类。
/// 在 App.xaml.cs 的 OnStartup 里调用 <see cref="AddApplicationServices"/>。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册本应用的所有业务服务。
    /// </summary>
    /// <param name="services">DI 容器的服务集合（由 Microsoft.Extensions.DependencyInjection 提供）。</param>
    /// <returns>同一个 <paramref name="services"/> 实例，方便链式调用。</returns>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // ── 1. 读取数据库连接字符串 ────────────────────────────────────────────
        //   DbSettingsProvider.GetConnectionString() 读取 dbsettings.json，
        //   返回形如 "Server=127.0.0.1;Port=3306;Database=xxx;Uid=xxx;Pwd=xxx;"
        var connectionString = DbSettingsProvider.GetConnectionString();

        // ── 2. 注册数据访问服务（Singleton）──────────────────────────────────
        //
        //   为什么用 Singleton？
        //   这三个服务内部只保存 connectionString 字符串，
        //   每次操作都重新 OpenAsync() 一个新连接（连接池管理），
        //   没有可变状态，多个页面共享同一实例完全安全，
        //   且避免每次打开页面都重新 new 对象的开销。
        //
        //   lambda 写法 sp => new XXX(connectionString)：
        //   sp 是 IServiceProvider，可以在 lambda 里从容器取其他服务，
        //   这里只需要传 connectionString，所以直接构造即可。
        /*/*
═══════════════════════════════════════════════════════════════
                AddSingleton 生命周期详解
═══════════════════════════════════════════════════════════════

services.AddSingleton<HomeViewModel>();

这意味着：
┌─────────────────────────────────────────────────────────────┐
│ 单例模式：整个应用程序生命周期中，只创建一个实例              │
│                                                             │
│  创建时机：第一次被请求时                                     │
│  销毁时机：应用程序关闭时                                     │
│  共享范围：整个应用程序所有地方都使用同一个实例                │
└─────────────────────────────────────────────────────────────┘

三种生命周期对比：
┌────────────────┬─────────────────┬─────────────────────────┐
│ 生命周期       │ 实例数量         │ 适用场景                 │
├────────────────┼─────────────────┼─────────────────────────┤
│ AddSingleton   │ 全局唯一1个      │ 全局配置、状态管理        │
│ AddScoped      │ 每个作用域1个    │ Web请求（WPF中不常用）    │
│ AddTransient   │ 每次获取都新建   │ 轻量级、无状态服务        │
└────────────────┴─────────────────┴─────────────────────────┘
* /*/

        services.AddSingleton<ManagementQueryService>(
            _ => new ManagementQueryService(connectionString));

        services.AddSingleton<ManagementInsertService>(
            _ => new ManagementInsertService(connectionString));

        services.AddSingleton<ManagementUpdateService>(
            _ => new ManagementUpdateService(connectionString));

        services.AddSingleton<ManagementDeleteService>(
            _ => new ManagementDeleteService(connectionString));

        // ── 3. 注册导航服务（页面缓存）────────────────────────────────────
        services.AddSingleton<CachedNavigationService>();

        // ── 3.1 结构化生产异常旁路（仅当前程序生命周期内存）────────────────
        services.AddSingleton(_ => OperationalEventMonitorOptionsLoader.Load());
        services.AddSingleton<IOperationalEventClock, SystemOperationalEventClock>();
        services.AddSingleton<OperationalEventStore>();
        services.AddSingleton<IOperationalEventStore>(
            provider => provider.GetRequiredService<OperationalEventStore>());
        services.AddSingleton<IOperationalEventReporter, OperationalEventReporter>();
        services.AddSingleton<OperationalEventFormatter>();
        services.AddSingleton<AttentionEventCenter>();

        // ── 4. 注册工件跟踪/设备数据写库服务（天车实时坐标不再持久化）─────────
        services.AddSingleton<PositionUpdateService>(
            _ => new PositionUpdateService(connectionString));

        // ── 5. 注册主线流程引擎（Singleton）───────────────────────────────
        //   引擎由 HomeViewModel 构造时创建（因为它需要 CraneConnectionCache + ManipulatorConnectionCache）
        //   ProductionFlowEngine 不在此处注册，由 HomeViewModel 内部管理生命周期

        // ── 6. 注册主页面 VM（Singleton）────────────────────────────────
        services.AddSingleton<HomeViewModel>();

        // ── 6.1 异常监控页面 VM（Singleton）──────────────────────────────
        services.AddSingleton<AttentionMonitorViewModel>();

        // ── 6.2 运行诊断页面 VM（Singleton）──────────────────────────────
        // 该VM只消费HomeViewModel提供的内存快照，不持有设备连接，也不创建后台轮询。
        services.AddSingleton<RuntimeDiagnosticsViewModel>();

        // ── 6.3 在制工件页面 VM（Singleton）──────────────────────────────
        // 该VM只聚合当前内存中的不可变展示快照，绝不拥有设备连接或业务控制入口。
        services.AddSingleton<InProcessWorkpieceOverviewViewModel>();

        return services;
    }
}
