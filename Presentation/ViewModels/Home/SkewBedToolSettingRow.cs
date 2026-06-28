using System;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 运动参数页里的单台斜床对刀开关。
/// 这是设备级设置: 不修改任务工艺, 只影响该斜床下一次写入加工参数时的模式码。
/// </summary>
public sealed class SkewBedToolSettingRow : ObservableObject
{
    private readonly Action<SkewBedToolSettingRow> _onChanged;
    private bool _isToolSetting;

    public SkewBedToolSettingRow(string code, string displayName, bool isToolSetting,
        Action<SkewBedToolSettingRow> onChanged)
    {
        Code = code;
        DisplayName = displayName;
        _isToolSetting = isToolSetting;
        _onChanged = onChanged;
    }

    public string Code { get; }
    public string DisplayName { get; }

    public bool IsToolSetting
    {
        get => _isToolSetting;
        set
        {
            if (SetField(ref _isToolSetting, value))
                _onChanged(this);
        }
    }
}
