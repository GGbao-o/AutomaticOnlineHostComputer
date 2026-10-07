using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>异常事件列表行，只读映射 OperationalEvent。</summary>
public sealed class OperationalEventRowViewModel : INotifyPropertyChanged
{
    private OperationalEvent _sourceEvent;
    private string? _fullDetailText;

    public OperationalEventRowViewModel(OperationalEvent source)
    {
        _sourceEvent = source;
    }

    internal OperationalEvent SourceEvent => _sourceEvent;

    internal void UpdateSource(OperationalEvent updated)
    {
        _sourceEvent = updated;
        _fullDetailText = null;
        OnPropertyChanged(nameof(LastOccurred));
        OnPropertyChanged(nameof(SeverityText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DeviceStation));
        OnPropertyChanged(nameof(Stage));
        OnPropertyChanged(nameof(WorkpieceText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(BusinessPausedText));
        OnPropertyChanged(nameof(CategoryText));
        OnPropertyChanged(nameof(IsRecoveredOrResolved));
    }

    public DateTime LastOccurred => _sourceEvent.LastOccurredAtUtc.ToLocalTime();
    public string SeverityText => _sourceEvent.Severity switch
    {
        OperationalEventSeverity.Critical => "严重",
        OperationalEventSeverity.Error => "错误",
        OperationalEventSeverity.Warning => "警告",
        _ => "信息"
    };
    public string CategoryText => _sourceEvent.Category.ToString("G");
    public string Title => string.IsNullOrWhiteSpace(_sourceEvent.Title) ? _sourceEvent.EventCode : _sourceEvent.Title;
    public string DeviceStation => string.IsNullOrWhiteSpace(_sourceEvent.DeviceType)
        ? _sourceEvent.Station
        : $"{_sourceEvent.DeviceType}{(string.IsNullOrWhiteSpace(_sourceEvent.DeviceNo) ? "" : _sourceEvent.DeviceNo)} {_sourceEvent.Station}".Trim();
    public string Stage => _sourceEvent.ActionStage;
    public string WorkpieceText
    {
        get
        {
            var wp = _sourceEvent.FirstEvidence?.Workpiece;
            if (wp == null) return "";
            var plate = wp.PlateNo.HasValue ? wp.PlateNo.Value : "";
            var seq = wp.Sequence.HasValue ? wp.Sequence.Value : "";
            if (string.IsNullOrWhiteSpace(plate) && string.IsNullOrWhiteSpace(seq)) return "";
            return $"{plate}-{seq}".Trim('-');
        }
    }
    public string CountText => _sourceEvent.OccurrenceCount > 1 ? $"×{_sourceEvent.OccurrenceCount}" : "";
    public string BusinessPausedText
    {
        get
        {
            var paused = _sourceEvent.LatestBusinessPaused;
            if (!paused.HasValue) return "未知";
            return paused.Value ? "已暂停" : "未暂停";
        }
    }
    public bool IsRecoveredOrResolved =>
        _sourceEvent.Category == OperationalEventCategory.AutomaticRecovery;

    public string FullDetailText
    {
        get
        {
            if (_fullDetailText == null)
            {
                try
                {
                    var formatter = new OperationalEventFormatter();
                    _fullDetailText = formatter.FormatFullDetail(_sourceEvent);
                }
                catch
                {
                    _fullDetailText = _sourceEvent.Title ?? "详情文本生成失败";
                }
            }
            return _fullDetailText;
        }
    }

    public string SearchText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(_sourceEvent.EventId);
            sb.AppendLine(_sourceEvent.FirstActionId);
            sb.AppendLine(_sourceEvent.LatestActionId);
            sb.AppendLine(_sourceEvent.EventCode);
            sb.AppendLine(_sourceEvent.Scope);
            sb.AppendLine(_sourceEvent.Engine);
            sb.AppendLine(_sourceEvent.DeviceType);
            sb.AppendLine(_sourceEvent.DeviceNo);
            sb.AppendLine(_sourceEvent.Station);
            sb.AppendLine(_sourceEvent.ActionStage);
            sb.AppendLine(_sourceEvent.Title);
            sb.AppendLine(WorkpieceText);
            sb.AppendLine(_sourceEvent.FirstDetailMessage);
            sb.AppendLine(_sourceEvent.LatestDetailMessage);
            sb.AppendLine(_sourceEvent.FirstResult);
            sb.AppendLine(_sourceEvent.LatestResult);
            return sb.ToString();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
