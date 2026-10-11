using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.Alarms;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

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
