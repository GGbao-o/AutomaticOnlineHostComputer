using AutomaticOnlineHostComputer.Presentation.ViewModels.Controller;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Crane;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Process;
using MySql.Data.MySqlClient;
using System.Data.Common;

namespace AutomaticOnlineHostComputer.Infrastructure.Data;

/// <summary>
/// 四个管理页面的数据查询服务。
/// </summary>
public sealed class ManagementQueryService
{
    /// <summary>默认首屏加载条数。</summary>
    public const int DefaultPageSize = 500;

    private readonly string _connectionString;

    public ManagementQueryService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// 异步查询天车管理列表。
    /// </summary>
    public async Task<List<CraneManagementRowVm>> GetCraneRowsAsync(int pageSize = DefaultPageSize)
    {
        const string sql = @"
SELECT
    id,
    IFNULL(line_no, 1)            AS line_no,
    IFNULL(crane_no, 0)           AS crane_no,
    IFNULL(name, '')              AS name,
    IFNULL(icon_path, '')         AS thumb,
    IFNULL(origin_x, 0)           AS x,
    IFNULL(origin_y, 0)           AS y,
    IFNULL(origin_z, 0)           AS z,
    IFNULL(ip, '')                AS ip,
    IFNULL(port, 0)               AS port,
    IFNULL(encoder_ip, '')        AS encoder_ip,
    IFNULL(encoder_port, 0)       AS encoder_port,
    IFNULL(encoder_no, 0)         AS encoder_no,
    IFNULL(width, 0)              AS width,
    IFNULL(abs_x_offset, 0)       AS abs_x_offset,
    IFNULL(ratio_x, 0)            AS ratio_x,
    IFNULL(ratio_y, 0)            AS ratio_y,
    IFNULL(ratio_z, 0)            AS ratio_z,
    IFNULL(start_x, 0)            AS start_x,
    IFNULL(end_x, 0)              AS end_x,
    IFNULL(limit_zp, 0)           AS limit_zp,
    IFNULL(limit_zn, 0)           AS limit_zn,
    IFNULL(pulse_x, 0)            AS pulse_x,
    CASE IFNULL(work_sta, 0)
        WHEN 1 THEN '启用'
        WHEN 2 THEN '检修'
        ELSE '停用'
    END                           AS state
FROM crane
ORDER BY id
LIMIT @pageSize;";

        var rows = new List<CraneManagementRowVm>();
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@pageSize", pageSize);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new CraneManagementRowVm
            {
                SourceId = GetInt(reader, "id"),
                Id = GetInt(reader, "id"),
                LineNo = GetInt(reader, "line_no"),
                CraneNo = GetInt(reader, "crane_no"),
                Name = GetString(reader, "name"),
                Thumb = GetString(reader, "thumb"),
                X = GetDouble(reader, "x"),
                Y = GetDouble(reader, "y"),
                Z = GetDouble(reader, "z"),
                Ip = GetString(reader, "ip"),
                Port = GetInt(reader, "port"),
                EncoderIp = GetString(reader, "encoder_ip"),
                EncoderPort = GetInt(reader, "encoder_port"),
                EncoderNo = GetInt(reader, "encoder_no"),
                Width = GetDouble(reader, "width"),
                AbsXOffset = GetInt(reader, "abs_x_offset"),
                RatioX = GetDouble(reader, "ratio_x"),
                RatioY = GetDouble(reader, "ratio_y"),
                RatioZ = GetDouble(reader, "ratio_z"),
                StartX = GetLong(reader, "start_x"),
                EndX = GetLong(reader, "end_x"),
                LimitZP = GetLong(reader, "limit_zp"),
                LimitZN = GetLong(reader, "limit_zn"),
                PulseX = GetDouble(reader, "pulse_x"),
                State = GetString(reader, "state")
            });
        }

        return rows;
    }

    /// <summary>
    /// 异步查询机器管理列表。
    /// </summary>
    public async Task<List<MachineManagementRowVm>> GetMachineRowsAsync(int pageSize = DefaultPageSize)
    {
        const string sql = @"
SELECT
    id,
    IFNULL(line_no, 1)        AS line_no,
    IFNULL(station_code, '')  AS station_code,
    IFNULL(name, '')          AS name,
    IFNULL(type_name, '')     AS type_name,
    IFNULL(area_name, '')     AS area_name,
    IFNULL(machine_no, 0)     AS machine_no,
    IFNULL(ip, '')            AS ip,
    IFNULL(port, 0)           AS port,
    CASE IFNULL(state, 0) WHEN 1 THEN '启用' WHEN 2 THEN '检修' ELSE '停用' END AS state_text,
    IFNULL(area_name, '')     AS position,
    IFNULL(icon_path, '')     AS thumb,
    IFNULL(x, 0)              AS x,
    IFNULL(y, 0)              AS y,
    IFNULL(z, 0)              AS z,
    IFNULL(safe_z_down, 0)    AS safe_z_down,
    IFNULL(safe_z_up, 0)      AS safe_z_up,
    IFNULL(absolute_pos, 0)   AS absolute_pos,
    IFNULL(x_dis, '0')        AS x_dis,
    IFNULL(y_dis, '0')        AS y_dis,
    IFNULL(z_dis, '0')        AS z_dis,
    IFNULL(dis_shake, 0)      AS dis_shake,
    IFNULL(process_range, '') AS process_range
FROM machine
ORDER BY id
LIMIT @pageSize;";

        var rows = new List<MachineManagementRowVm>();
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@pageSize", pageSize);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new MachineManagementRowVm
            {
                SourceId = GetInt(reader, "id"),
                Id = GetInt(reader, "id"),
                LineNo = GetInt(reader, "line_no"),
                StationCode = GetString(reader, "station_code"),
                Name = GetString(reader, "name"),
                TypeName = GetString(reader, "type_name"),
                AreaName = GetString(reader, "area_name"),
                MachineNo = GetInt(reader, "machine_no"),
                Ip = GetString(reader, "ip"),
                Port = GetInt(reader, "port"),
                State = GetString(reader, "state_text"),
                Position = GetString(reader, "position"),
                Thumb = GetString(reader, "thumb"),
                X = GetDouble(reader, "x"),
                Y = GetDouble(reader, "y"),
                Z = GetDouble(reader, "z"),
                SafeZDown = GetInt(reader, "safe_z_down"),
                SafeZUp = GetInt(reader, "safe_z_up"),
                AbsolutePos = GetInt(reader, "absolute_pos"),
                XOffset = ParseDouble(GetString(reader, "x_dis")),
                YOffset = ParseDouble(GetString(reader, "y_dis")),
                ZOffset = ParseDouble(GetString(reader, "z_dis")),
                Shake = GetDouble(reader, "dis_shake"),
                ProcessRange = GetString(reader, "process_range")
            });
        }

        return rows;
    }

    /// <summary>
    /// 异步查询控制器管理列表。
    /// </summary>
    public async Task<List<ControllerManagementRowVm>> GetControllerRowsAsync(int pageSize = DefaultPageSize)
    {
        const string sql = @"
SELECT
    id,
    IFNULL(device_no, 0)      AS device_no,
    IFNULL(name, '')          AS name,
    IFNULL(icon_path, '')     AS thumb,
    IFNULL(type_name, '')     AS type_name,
    IFNULL(ip, '')            AS ip,
    IFNULL(port, 0)           AS port,
    CASE IFNULL(state, 0)
        WHEN 1 THEN '启用'
        WHEN 2 THEN '检修'
        ELSE '停用'
    END                       AS state_text
FROM controller
ORDER BY id
LIMIT @pageSize;";

        var rows = new List<ControllerManagementRowVm>();
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@pageSize", pageSize);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new ControllerManagementRowVm
            {
                SourceId = GetInt(reader, "id"),
                Id = GetInt(reader, "id"),
                DeviceNo = GetInt(reader, "device_no"),
                Name = GetString(reader, "name"),
                Thumb = GetString(reader, "thumb"),
                Type = GetString(reader, "type_name"),
                Ip = GetString(reader, "ip"),
                Port = GetInt(reader, "port"),
                State = GetString(reader, "state_text")
            });
        }

        return rows;
    }

    /// <summary>
    /// 异步查询工艺管理列表（按工艺名分组后展开到 Step1~Step10）。
    /// </summary>
    public async Task<List<ProcessManagementRowVm>> GetProcessRowsAsync(int pageSize = DefaultPageSize)
    {
        const string sql = @"
SELECT
    id,
    process_name,
    step_no,
    IFNULL(step_name, '')      AS step_name,
    IFNULL(execute_time, 0)    AS execute_time,
    IFNULL(device, '')         AS device,
    IFNULL(enabled, 0)         AS enabled,
    IFNULL(z_axis_back_home, 0) AS z_axis_back_home,
    IFNULL(safe_position, '')  AS safe_position,
    IFNULL(machine_no, 0)      AS machine_no
FROM process_route
ORDER BY process_name, step_no, id
LIMIT @pageSize;";

        var raw = new List<(int SourceId, string Name, int StepNo, string StepName, int ExecuteTime, string Device, bool Enabled, bool ZBackHome, string SafePosition, int MachineNo)>();

        await using (var conn = new MySqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@pageSize", pageSize);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                raw.Add((
                    GetInt(reader, "id"),
                    GetString(reader, "process_name"),
                    GetInt(reader, "step_no"),
                    GetString(reader, "step_name"),
                    GetInt(reader, "execute_time"),
                    GetString(reader, "device"),
                    GetInt(reader, "enabled") == 1,
                    GetInt(reader, "z_axis_back_home") == 1,
                    GetString(reader, "safe_position"),
                    GetInt(reader, "machine_no")));
            }
        }

        var result = new List<ProcessManagementRowVm>();
        var groups = raw.GroupBy(x => x.Name).ToList();

        for (var i = 0; i < groups.Count; i++)
        {
            var first = groups[i].OrderBy(x => x.StepNo).First();
            var row = new ProcessManagementRowVm
            {
                SourceId = first.SourceId,
                Id = i + 1,
                Name = groups[i].Key,
                StepNo = first.StepNo,
                StepName = first.StepName,
                ExecuteTime = first.ExecuteTime,
                Device = first.Device,
                Enabled = first.Enabled,
                ZAxisBackHome = first.ZBackHome,
                SafePosition = first.SafePosition,
                MachineNo = first.MachineNo
            };

            foreach (var item in groups[i])
            {
                SetStep(row, item.StepNo, item.StepName);
            }

            result.Add(row);
        }

        return result;
    }

    private static void SetStep(ProcessManagementRowVm row, int stepNo, string stepName)
    {
        switch (stepNo)
        {
            case 1: row.Step1 = stepName; break;
            case 2: row.Step2 = stepName; break;
            case 3: row.Step3 = stepName; break;
            case 4: row.Step4 = stepName; break;
            case 5: row.Step5 = stepName; break;
            case 6: row.Step6 = stepName; break;
            case 7: row.Step7 = stepName; break;
            case 8: row.Step8 = stepName; break;
            case 9: row.Step9 = stepName; break;
            case 10: row.Step10 = stepName; break;
        }
    }

    private static int GetInt(DbDataReader reader, string column)
    {
        var value = reader[column];
        return value == DBNull.Value ? 0 : Convert.ToInt32(value);
    }

    private static double GetDouble(DbDataReader reader, string column)
    {
        var value = reader[column];
        return value == DBNull.Value ? 0D : Convert.ToDouble(value);
    }

    private static long GetLong(DbDataReader reader, string column)
    {
        var value = reader[column];
        return value == DBNull.Value ? 0L : Convert.ToInt64(value);
    }

    private static string GetString(DbDataReader reader, string column)
    {
        var value = reader[column];
        return value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, out var result) ? result : 0;
    }
}
