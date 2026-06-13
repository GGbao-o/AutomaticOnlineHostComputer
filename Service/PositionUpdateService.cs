using System;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 天车/机械手当前位置异步写库服务。
/// 所有 DB 操作均为 fire-and-forget，失败不打紧（控制台日志 + 不影响主流程）。
/// </summary>
public sealed class PositionUpdateService
{
    private readonly string _connStr;

    public PositionUpdateService(string connectionString)
    {
        _connStr = connectionString;
    }

    /// <summary>
    /// 更新 crane 表的 current_x/y/z（天车/机械手当前位置）。
    /// 异步执行，DB 不可用时静默忽略。
    /// </summary>
    public async Task UpdateCranePositionAsync(string craneName, int x, int y, int z)
    {
        try
        {
            const string sql = "UPDATE crane SET current_x=@x, current_y=@y, current_z=@z WHERE name=@name LIMIT 1";
            await using var conn = new MySqlConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@x", x);
            cmd.Parameters.AddWithValue("@y", y);
            cmd.Parameters.AddWithValue("@z", z);
            cmd.Parameters.AddWithValue("@name", craneName);
            int rows = await cmd.ExecuteNonQueryAsync();
            Console.WriteLine(rows > 0
                ? $"[PositionUpdate] ✔ crane.{craneName} 位置已更新: X={x} Y={y} Z={z}"
                : $"[PositionUpdate] ⚠ crane.{craneName} 未匹配到记录（name字段不匹配？）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PositionUpdate] ✘ crane.{craneName} 写库失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新 machine 表的 current_x/y/z（设备当前位置）。
    /// 异步执行，DB 不可用时静默忽略。
    /// </summary>
    public async Task UpdateMachinePositionAsync(string stationCode, int x, int y, int z)
    {
        try
        {
            const string sql = "UPDATE machine SET current_x=@x, current_y=@y, current_z=@z WHERE station_code=@code LIMIT 1";
            await using var conn = new MySqlConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@x", x);
            cmd.Parameters.AddWithValue("@y", y);
            cmd.Parameters.AddWithValue("@z", z);
            cmd.Parameters.AddWithValue("@code", stationCode);
            int rows = await cmd.ExecuteNonQueryAsync();
            Console.WriteLine(rows > 0
                ? $"[PositionUpdate] ✔ machine.{stationCode} 位置已更新: X={x} Y={y} Z={z}"
                : $"[PositionUpdate] ⚠ machine.{stationCode} 未匹配到记录（station_code不匹配？）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PositionUpdate] ✘ machine.{stationCode} 写库失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 插入工件跟踪记录。DB 不可用时静默忽略。
    /// </summary>
    public async Task InsertWorkpieceTrackAsync(
        string plateNo, string sequence, string stage, string processType,
        double length, double diameter, double plugHole, string marking,
        int? assignedLine, string? loadMethod, bool needsBalance)
    {
        try
        {
            const string sql = @"
INSERT INTO workpiece_track
(plate_no, sequence, current_stage, process_type, length, diameter, plug_hole, marking_content, assigned_line, load_method, needs_balance)
VALUES
(@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11);";
            await using var conn = new MySqlConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@p1", plateNo);
            cmd.Parameters.AddWithValue("@p2", sequence);
            cmd.Parameters.AddWithValue("@p3", stage);
            cmd.Parameters.AddWithValue("@p4", processType);
            cmd.Parameters.AddWithValue("@p5", length);
            cmd.Parameters.AddWithValue("@p6", diameter);
            cmd.Parameters.AddWithValue("@p7", plugHole);
            cmd.Parameters.AddWithValue("@p8", marking);
            cmd.Parameters.AddWithValue("@p9", (object?)assignedLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p10", (object?)loadMethod ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p11", needsBalance ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"[PositionUpdate] ✔ workpiece_track 已记录: {plateNo} → {stage}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PositionUpdate] ✘ workpiece_track 写库失败: {ex.Message}");
        }
    }
}
