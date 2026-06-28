using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 斜床加工完成导出。
/// 触发点由后端引擎控制在“加工中→请求下料”时刻；本类只追加job2记录, 不参与任何运动/锁/缓存业务判断。
/// </summary>
internal static class SkewCompletionExportHelper
{
    // 两条后端引擎可能在同一时刻发现斜床完工。
    // job2 是同一个文件, 这里仅串行化本进程内的追加写入, 不改变任何下料/缓存/运动业务判断。
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static async Task<bool> TryAppendAsync(
        MotionConfig cfg,
        string stationCode,
        WorkpieceCache wp,
        CancellationToken ct)
    {
        if (!cfg.SkewCompletionExport.Enabled)
            return true;

        if (!cfg.SkewCompletionExport.TryGetMachineCode(stationCode, out var machineCode)
            || string.IsNullOrWhiteSpace(machineCode))
        {
            Console.WriteLine($"[斜床完工导出] ⚠ {stationCode} 未配置机器编码, 暂不写job2 {wp.IdentityText}");
            return false;
        }

        string path = cfg.SkewCompletionExport.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine($"[斜床完工导出] ⚠ 文件路径未配置, 暂不写job2 {stationCode} {wp.IdentityText}");
            return false;
        }

        bool gotWriteLock = false;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string line = $"{wp.PlateNo},{wp.Sequence},{machineCode},{time}{Environment.NewLine}";

            // 多台斜床可能近同时完成。先拿进程内写锁, 防止两条线同时追加同一个job2文件导致行内容交叉。
            await WriteLock.WaitAsync(ct);
            gotWriteLock = true;

            // 文件仍允许ERP/人工工具读取。若文件被外部进程独占占用, 返回false, 下轮WaitingUnload继续补写。
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);

            Console.WriteLine($"[斜床完工导出] ✓ {stationCode}->{machineCode} {wp.IdentityText} 已追加 {path}: {line.TrimEnd()}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[斜床完工导出] ⚠ 写入失败, 下轮重试 {stationCode} {wp.IdentityText}: {ex.Message}");
            return false;
        }
        finally
        {
            if (gotWriteLock)
                WriteLock.Release();
        }
    }
}
