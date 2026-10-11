using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.Alarms;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 只管理异常弹窗的展示与独立只读刷新，不暂停/恢复引擎，不写PLC，不改变动作结案。
/// _read由主页按天车或机械手连接创建；相同设备数字不代表相同IP/服务。
/// 弹窗关闭取消采样并解绑事件，业务异常收尾仍由引擎继续完成，不依赖窗口是否保留。
/// </summary>
internal sealed class SafetyAlarmDialogViewModel : ObservableObject, IDisposable
{
    private readonly FlowAlarmPresentationContext _context;
    private readonly Func<int, CancellationToken, Task<CraneAlarmSnapshot>> _read;
    private readonly Action<Action> _dispatch;
    private readonly CancellationTokenSource _closed = new();
    private bool _disposed;
    private bool _isRefreshing;
    private CraneAlarmSnapshot? _sample;
    private SafetyAlarmSections _sections;

    public SafetyAlarmDialogViewModel(FlowAlarmPresentationContext context,
        Func<int, CancellationToken, Task<CraneAlarmSnapshot>> read, Action<Action> dispatch)
    {
        _context = context;
        _read = read;
        _dispatch = dispatch;
        _sections = SafetyAlarmFormatter.Sections(context.Snapshot(), null);
        _context.Changed += ContextChanged;
        UpdateSections(); // 订阅前完成的收尾也必须显示。
    }

    public SafetyAlarmSections Sections => _sections;
    public bool IsRefreshing => _isRefreshing;
    public bool CanRefresh => !_isRefreshing && !_disposed;
    public string RefreshStatus => _isRefreshing ? "正在只读采样（最多2秒）…" : "";
    public string CopyText => _sections.FullText;

    private void ContextChanged()
    {
        try { _dispatch(() => { if (!_disposed) UpdateSections(); }); } catch { }
    }

    private void UpdateSections()
    {
        _sections = SafetyAlarmFormatter.Sections(_context.Snapshot(), _sample);
        OnPropertyChanged(nameof(Sections));
    }

    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        _isRefreshing = true;
        RefreshProperties();
        try
        {
            var details = _context.Snapshot();
            // 连接类型已由主页选择；这里传机械手编号或天车编号，仅读当前反馈和坐标。
            // 新采样只更新现场区域，不能覆盖故障时X11/运动步骤或让旧任务重新生效。
            var sample = await _read(details.ManipulatorNo ?? details.CraneNo, _closed.Token);
            if (_disposed) return;
            _sample = sample;
            UpdateSections();
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _sample = CraneAlarmSnapshot.Unknown(ex.Message);
                UpdateSections();
            }
        }
        finally
        {
            _isRefreshing = false;
            if (!_disposed) RefreshProperties();
        }
    }

    private void RefreshProperties()
    {
        OnPropertyChanged(nameof(IsRefreshing));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(RefreshStatus));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Changed -= ContextChanged;
        _closed.Cancel();
        _closed.Dispose();
    }
}
