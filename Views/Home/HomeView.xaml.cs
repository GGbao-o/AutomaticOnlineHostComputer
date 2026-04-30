using System.Windows.Controls;
using System.Windows;
using AutomaticOnlineHostComputer.Views.Home.Dialogs;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer.Views.Home;

public partial class HomeView : UserControl
{
    private readonly HomeViewModel _viewModel;

    public HomeView()
    {
        InitializeComponent();
        
        _viewModel = App.Services.GetRequiredService<HomeViewModel>();//从di容器获取
        DataContext = _viewModel;//绑定viewmodel
        
        Loaded += async (_, __) => await _viewModel.LoadAsync();//窗口加载时候触发
    }

    /// <summary>
    /// 添加按钮
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddTaskDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// 切换页面
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void RouteSelect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RouteSelectDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}