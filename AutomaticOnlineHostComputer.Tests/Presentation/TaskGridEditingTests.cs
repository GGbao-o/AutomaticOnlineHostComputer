using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

/// <summary>解析真实任务表列，在STA上验证显示列只读及数字列实际写回；不启动应用/连接设备。</summary>
public sealed class TaskGridEditingTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Theory]
    [InlineData("QueueOrderText")]
    [InlineData("StartSourceText")]
    [InlineData("AssignedLineText")]
    [InlineData("BalancingText")]
    [InlineData("SkipBoringText")]
    public Task Computed_display_column_cannot_enter_editing_and_binds_one_way(string property) => OnSta(() =>
    {
        var column = ParseColumn(TextColumns().Single(x => BindingPath(x) == property));
        var binding = Assert.IsType<Binding>(column.Binding);
        Assert.True(column.IsReadOnly);
        Assert.Equal(BindingMode.OneWay, binding.Mode);
        Assert.False(typeof(TaskRowViewModel).GetProperty(property)!.CanWrite);

        var row = new TaskRowViewModel { ForceBalancing = true, AssignedLine = 2 };
        var display = Assert.IsType<TextBlock>(Generate(column, "GenerateElement", row));
        display.DataContext = row;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Assert.Equal(typeof(TaskRowViewModel).GetProperty(property)!.GetValue(row)?.ToString(), display.Text);
        Assert.False(BindingOperations.GetBindingExpression(display, TextBlock.TextProperty)!.HasError);
    });

    [Theory]
    [InlineData("Length", 1234.5)]
    [InlineData("Diameter", 278.5)]
    public Task Length_and_diameter_editors_still_commit_numbers_to_the_existing_task(string property, double value) => OnSta(() =>
    {
        var column = ParseColumn(TextColumns().Single(x => BindingPath(x) == property));
        Assert.False(column.IsReadOnly);
        var row = new TaskRowViewModel { Length = 900, Diameter = 100 };
        var editor = Assert.IsType<TextBox>(Generate(column, "GenerateEditingElement", row));
        editor.DataContext = row;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        editor.SetCurrentValue(TextBox.TextProperty, value.ToString(CultureInfo.InvariantCulture));
        var expression = BindingOperations.GetBindingExpression(editor, TextBox.TextProperty)!;
        expression.UpdateSource();
        Assert.False(expression.HasError);
        Assert.Equal(value, (double)typeof(TaskRowViewModel).GetProperty(property)!.GetValue(row)!);
    });

    [Fact]
    public Task Every_bound_text_property_keeps_the_correct_editing_policy() => OnSta(() =>
    {
        foreach (var node in TextColumns())
        {
            string property = BindingPath(node);
            var info = typeof(TaskRowViewModel).GetProperty(property);
            Assert.NotNull(info);
            var column = ParseColumn(node);
            var binding = Assert.IsType<Binding>(column.Binding);
            if (info.CanWrite)
            {
                Assert.False(column.IsReadOnly);
                Assert.True(binding.Mode is BindingMode.Default or BindingMode.TwoWay);
            }
            else
            {
                Assert.True(column.IsReadOnly, property + "必须禁止进入编辑");
                Assert.Equal(BindingMode.OneWay, binding.Mode);
            }
        }
    });

    [Fact]
    public void Whole_grid_and_existing_action_buttons_remain_enabled()
    {
        XElement grid = TaskGrid();
        Assert.NotEqual("True", (string?)grid.Attribute("IsReadOnly"));
        Assert.NotEqual("False", (string?)grid.Attribute("IsEnabled"));
        foreach (string command in new[] { "ToggleRunCommand", "DeleteCommand" })
        {
            var button = Assert.Single(grid.Descendants(Ui + "Button")
                .Where(x => (string?)x.Attribute("Command") == "{Binding " + command + "}"));
            Assert.NotEqual("False", (string?)button.Attribute("IsEnabled"));
        }
    }

    private static XElement TaskGrid() => XDocument.Load(Path.Combine(RepositoryRoot.Find(), "Views", "Home", "HomeView.xaml"))
        .Descendants(Ui + "DataGrid").Single(x => (string?)x.Attribute("ItemsSource") == "{Binding TaskRows}");
    private static IEnumerable<XElement> TextColumns() => TaskGrid().Descendants(Ui + "DataGridTextColumn");
    private static string BindingPath(XElement column)
        => ((string)column.Attribute("Binding")!)["{Binding ".Length..].TrimEnd('}').Split(',')[0].Trim();
    private static DataGridTextColumn ParseColumn(XElement column) => (DataGridTextColumn)XamlReader.Parse(column.ToString());
    private static object Generate(DataGridTextColumn column, string method, object row)
        => typeof(DataGridTextColumn).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(column, new object[] { new DataGridCell(), row })!;

    private static async Task OnSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                action();
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
