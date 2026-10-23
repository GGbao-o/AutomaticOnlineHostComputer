using System.Text.Json;
using System.Xml.Linq;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ManualJogPageTests
{
    [Fact]
    public void Config_defaults_old_json_and_page_edit_round_trip()
    {
        var config = JsonSerializer.Deserialize<MotionConfig>("{}")!;
        Assert.Equal(1000, config.ManualControl.JogPulseDurationMs);
        var vm = new ConfigPageViewModel(config);
        vm.ManualJogPulseDurationMs = 12000;
        Assert.Equal(12000, config.ManualControl.JogPulseDurationMs);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(config, options);
        Assert.Contains("\"jogPulseDurationMs\":12000", json);
        Assert.Equal(12000, JsonSerializer.Deserialize<MotionConfig>(json, options)!.ManualControl.JogPulseDurationMs);
    }

    [Fact]
    public void Manipulator_has_no_x_jog_even_when_command_is_invoked_directly()
    {
        var config = new MotionConfig();
        var vm = new CraneManualControlViewModel(new CraneConnectionCache(config), new ManipulatorConnectionCache(config), config);
        vm.SelectedDevice = vm.DeviceItems.Single(x => x.DeviceType == ManualDeviceType.Manipulator && x.Index == 1);
        Assert.False(vm.CanJogX);
        Assert.True(vm.CanJog);
        string original = vm.JogStatus;
        vm.JogXPlusCommand.Execute(null);
        vm.JogXMinusCommand.Execute(null);
        Assert.Equal(original, vm.JogStatus); // 未装载IP；若错误地尝试访问服务，状态会变成失败。
    }

    [Fact]
    public void Six_new_buttons_are_separate_and_existing_stop_buttons_are_not_disabled_by_jog()
    {
        string root = RepositoryRoot.Find();
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var page = XDocument.Load(Path.Combine(root, "Views", "Motion", "Controls", "ManualMotionControlPanel.xaml"));
        var group = page.Descendants(ui + "GroupBox").Single(x => (string?)x.Attribute("Header") == "设备操作");
        var jogButtons = group.Descendants(ui + "Button").Where(x => ((string?)x.Attribute("Content"))?.StartsWith("JOG ") == true).ToArray();
        Assert.Equal(6, jogButtons.Length);
        foreach (var button in jogButtons)
            Assert.Equal((string?)button.Attribute("Content") is "JOG X+" or "JOG X−"
                ? "{Binding ManualControl.CanJogX}" : "{Binding ManualControl.CanJog}", (string?)button.Attribute("IsEnabled"));
        foreach (string label in new[] { "减速停止", "急停" })
            Assert.Null(group.Descendants(ui + "Button").Single(x => (string?)x.Attribute("Content") == label).Attribute("IsEnabled"));
        var config = XDocument.Load(Path.Combine(root, "Views", "Config", "ConfigPageView.xaml"));
        Assert.Single(config.Descendants(ui + "TextBox").Where(x => ((string?)x.Attribute("Text"))?.StartsWith("{Binding ManualJogPulseDurationMs,") == true));
    }
}
