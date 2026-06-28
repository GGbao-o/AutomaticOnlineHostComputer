using System.Windows;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;

namespace AutomaticOnlineHostComputer.Views.Config;

public partial class ConfigPageView : Window
{
    public ConfigPageView(ConfigPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
