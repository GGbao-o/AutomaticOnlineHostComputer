using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Infrastructure.Navigation;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer;

public partial class MainWindow : Window
{
    private readonly CachedNavigationService _navigationService;
    private readonly OperationalEventNavigationState _navState;

    public MainWindow()
    {
        InitializeComponent();

        _navigationService = App.Services.GetRequiredService<CachedNavigationService>();
        _navState = App.Services.GetRequiredService<OperationalEventNavigationState>();
        _navState.PropertyChanged += OnNavStateChanged;

        Console.WriteLine("MainWindow: 显示默认页面...");
        ContentHost.Content = _navigationService.GetOrCreatePage("main");
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is null) return;
        ContentHost.Content = _navigationService.GetOrCreatePage(menuItem.Tag.ToString());
    }

    private void OnNavStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationalEventNavigationState.UnseenDisplayText)) return;
        try
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                string badge = _navState.UnseenDisplayText;
                AttentionMonitorMenuItem.Header = string.IsNullOrWhiteSpace(badge)
                    ? "异常监控"
                    : $"异常监控 ({badge})";
            }));
        }
        catch { }
    }
}