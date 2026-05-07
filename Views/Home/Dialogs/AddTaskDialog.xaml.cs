using System.Windows;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class AddTaskDialog : Window
{
    private readonly AddTaskDialogViewModel _vm;

    /// <summary>
    /// 点击确定后返回新增任务对象；取消时为 null。
    /// </summary>
    public TaskRowViewModel? CreatedTask { get; private set; }

    public AddTaskDialog()
    {
        InitializeComponent();
        _vm = new AddTaskDialogViewModel();
        DataContext = _vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.Validate(out var message))
        {
            MessageBox.Show(this, message, "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CreatedTask = _vm.ToTaskRow();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
