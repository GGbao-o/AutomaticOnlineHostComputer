using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Infrastructure.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer;

public partial class MainWindow : Window
{
    /// <summary>
    /// 菜单导航服务（带页面缓存）。
    /// </summary>
    private readonly CachedNavigationService _navigationService;

    public MainWindow()
    {
        InitializeComponent();
        

        // 从 DI 容器获取导航服务。
        // 注意：CachedNavigationService 是 Singleton，
        // 因此其内部页面缓存会在应用生命周期内保持。
        _navigationService = App.Services.GetRequiredService<CachedNavigationService>();

        // 默认打开主界面（会走缓存逻辑）。
        Console.WriteLine("MainWindow: 显示默认页面...");
        ContentHost.Content = _navigationService.GetOrCreatePage("main");
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is null) return;

        // 统一通过导航服务获取页面：首次创建，后续复用。
        // 这样可避免每次点击菜单都 new 页面导致状态丢失和重复查询。
        ContentHost.Content = _navigationService.GetOrCreatePage(menuItem.Tag.ToString());
    }
}