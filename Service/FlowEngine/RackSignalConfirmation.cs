using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Settings = AutomaticOnlineHostComputer.Infrastructure.Config.MotionConfig.RackSignalConfirmationSection;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>一个PLC点位的一组连续确认。候选在每次新读成功时更新，动作每次复核另建一组。</summary>
internal sealed class RackSignalConfirmation
{
    private readonly object _gate = new();
    private int _streak;
    private int _attempt;
    private int _required;
    private int _intervalMs;
    private long _firstSample;
    private long _lastSample;
    private long _lastAccepted;

    public bool Observe(string point, bool value, bool expected, Settings settings, bool logEverySample = false,
        bool usesExplicitDelay = false)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_required != settings.ConsecutiveReads || _intervalMs != settings.SampleIntervalMs ||
                (_lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample, now) > Freshness(settings)))
            {
                ResetCore();
                _required = settings.ConsecutiveReads;
                _intervalMs = settings.SampleIntervalMs;
            }
            if (_firstSample == 0) _firstSample = now;
            _lastSample = now;
            _attempt++;
            int previous = _streak;
            // 反值立即打断连续计数。相隔太近的成功报文不能凑数，例如同轮锁外及锁后复扫。
            bool counted = value == expected && (usesExplicitDelay || _lastAccepted == 0 ||
                Stopwatch.GetElapsedTime(_lastAccepted, now).TotalMilliseconds >= _intervalMs);
            if (value != expected)
            {
                _streak = 0;
                _lastAccepted = 0;
            }
            else if (counted)
            {
                _lastAccepted = now;
                if (_streak < _required) _streak++;
            }

            if (logEverySample || _attempt == 1 || (counted && previous != _streak) ||
                (value != expected && previous > 0) || (logEverySample == false && _attempt % 50 == 0 && _streak < _required))
            {
                Console.WriteLine($"[料架确认] {point} 第{_attempt}次 实读={(value ? 1 : 0)} 要求={(expected ? 1 : 0)} " +
                    $"连续={_streak}/{_required} 等待={Stopwatch.GetElapsedTime(_firstSample, now).TotalMilliseconds:F0}ms" +
                    (value != expected ? "（计数清零）" : !counted ? "（间隔不足）" : ""));
            }
            return _streak >= _required;
        }
    }

    public bool IsConfirmed(Settings settings)
    {
        lock (_gate)
            return _required == settings.ConsecutiveReads && _intervalMs == settings.SampleIntervalMs &&
                _streak >= _required && _lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample) <= Freshness(settings);
    }

    public void Reset(string? failure = null)
    {
        lock (_gate)
        {
            if (failure != null && _attempt > 0)
                Console.WriteLine($"[料架确认] {failure}，连续计数清零");
            ResetCore();
        }
    }

    private void ResetCore()
    {
        _attempt = _streak = 0;
        _firstSample = _lastSample = _lastAccepted = 0;
    }

    // 候选跟随现有主循环，可能遇到设备轮询；长时间无新样本时使旧许可失效。
    private static TimeSpan Freshness(Settings settings) => TimeSpan.FromMilliseconds(Math.Max(5000, 3L * settings.SampleIntervalMs));

    public static async Task<bool> CheckAsync(Func<CancellationToken, Task<bool>> read, bool expected,
        string point, Settings settings, CancellationToken ct, bool waitUntilConfirmed = false)
    {
        var confirmation = new RackSignalConfirmation();
        do
        {
            ct.ThrowIfCancellationRequested();
            bool value;
            try { value = await read(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                confirmation.Reset($"{point} 读取失败（{ex.Message}）");
                throw;
            }
            ct.ThrowIfCancellationRequested();
            if (confirmation.Observe(point, value, expected, settings,
                logEverySample: !waitUntilConfirmed, usesExplicitDelay: true))
                return true;
            if (value != expected && !waitUntilConfirmed)
                return false;
            await Task.Delay(settings.SampleIntervalMs, ct);
        } while (true);
    }

    /// <summary>复用MC63块读。缺字必须失败，尤其不能把缺失的有板位当成空位。</summary>
    public static async Task<int> ReadMc63RackMaskAsync(MitsubishiMcClient client, CancellationToken ct)
    {
        var result = await client.ReadMAlignedWordAsync(800, 2, ct);
        if (result.IntValues.Length < 2)
            throw new InvalidOperationException("MC63料架读取失败：M800起返回字数不足2");
        return (result.IntValues[0] & 0xffff) | (result.IntValues[1] << 16);
    }

    public static bool Bit(int mask, int address) => (mask & (1 << (address - 800))) != 0;

    public static string[] TransferStations(int line) => line == 1
        ? new[] { "ST105", "ST101", "ST106" }
        : new[] { "ST016", "ST017", "ST018" };

    /// <summary>三个架位分别计数；锁后限一组，最终选位等待稳定空位或确认全满。</summary>
    public static async Task<string?> FindEmptyTransferRackAsync(Func<CancellationToken, Task<int>> read,
        int line, string phase, Settings settings, CancellationToken ct, bool singleWindow = false,
        bool stopWhenFull = false, bool retryReadFailures = false)
    {
        string[] stations = TransferStations(line);
        int firstAddress = line == 1 ? 811 : 814;
        RackSignalConfirmation[] empty = { new(), new(), new() };
        var full = new RackSignalConfirmation();
        for (int sample = 1; ; sample++)
        {
            ct.ThrowIfCancellationRequested();
            int mask;
            try { mask = await read(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (!retryReadFailures || sample == 1 || sample % 50 == 0)
                    Console.WriteLine($"[料架确认] {line}号线 {phase} 第{sample}次读取MC63失败，空位未确认：{ex.Message}");
                if (!retryReadFailures) throw;
                // 打号前锁外预检可以等待后台恢复连接，失败前的空位样本全部作废。
                foreach (var confirmation in empty) confirmation.Reset();
                await Task.Delay(settings.SampleIntervalMs, ct);
                continue;
            }
            ct.ThrowIfCancellationRequested();
            string? selected = null;
            bool allOccupied = true;
            for (int i = 0; i < stations.Length; i++)
            {
                int address = firstAddress + i;
                bool occupied = Bit(mask, address);
                allOccupied &= occupied;
                if (empty[i].Observe($"{line}号线 {phase} {stations[i]}/MC63 M{address}", occupied, false, settings,
                    logEverySample: singleWindow, usesExplicitDelay: true))
                    selected ??= stations[i];
            }
            if (selected != null) return selected;
            if (stopWhenFull && full.Observe($"{line}号线 {phase} 三架全满", allOccupied, true, settings, usesExplicitDelay: true))
                return null;
            if (singleWindow && sample >= settings.ConsecutiveReads) return null;
            await Task.Delay(settings.SampleIntervalMs, ct);
        }
    }
}
