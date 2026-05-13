using AutomaticOnlineHostComputer.Domain.Models;
using MySql.Data.MySqlClient;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 四个管理页面的新增写入服务。
/// 说明：
/// 1) 统一封装 INSERT 逻辑，避免散落在按钮事件中。
/// 2) 通过输入模型解耦 UI 与数据访问层。
/// </summary>
public sealed class ManagementInsertService
{
    private readonly string _connectionString;

    public ManagementInsertService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// 新增天车。
    /// </summary>
    public async Task AddCraneAsync(AddCraneInput input)
    {
        const string sql = @"
INSERT INTO crane
(line_no, crane_no, name, ip, port, encoder_ip, encoder_port, encoder_no, origin_x, origin_y, origin_z, width, abs_x_offset, ratio_x, ratio_y, ratio_z, start_x, end_x, limit_zp, limit_zn, pulse_x, current_x, current_y, current_z, work_sta)
VALUES
(@line_no, @crane_no, @name, @ip, @port, @encoder_ip, @encoder_port, @encoder_no, @origin_x, @origin_y, @origin_z, @width, @abs_x_offset, @ratio_x, @ratio_y, @ratio_z, @start_x, @end_x, @limit_zp, @limit_zn, @pulse_x, @current_x, @current_y, @current_z, @work_sta);";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@line_no", input.LineNo);
        cmd.Parameters.AddWithValue("@crane_no", input.CraneNo);
        cmd.Parameters.AddWithValue("@name", input.Name);
        cmd.Parameters.AddWithValue("@ip", input.Ip);
        cmd.Parameters.AddWithValue("@port", input.Port);
        cmd.Parameters.AddWithValue("@encoder_ip", input.EncoderIp);
        cmd.Parameters.AddWithValue("@encoder_port", input.EncoderPort);
        cmd.Parameters.AddWithValue("@encoder_no", input.EncoderNo);
        cmd.Parameters.AddWithValue("@origin_x", input.OriginX);
        cmd.Parameters.AddWithValue("@origin_y", input.OriginY);
        cmd.Parameters.AddWithValue("@origin_z", input.OriginZ);
        cmd.Parameters.AddWithValue("@width", input.Width);
        cmd.Parameters.AddWithValue("@abs_x_offset", input.AbsXOffset);
        cmd.Parameters.AddWithValue("@ratio_x", input.RatioX);
        cmd.Parameters.AddWithValue("@ratio_y", input.RatioY);
        cmd.Parameters.AddWithValue("@ratio_z", input.RatioZ);
        cmd.Parameters.AddWithValue("@start_x", input.StartX);
        cmd.Parameters.AddWithValue("@end_x", input.EndX);
        cmd.Parameters.AddWithValue("@limit_zp", input.LimitZP);
        cmd.Parameters.AddWithValue("@limit_zn", input.LimitZN);
        cmd.Parameters.AddWithValue("@pulse_x", input.PulseX);
        cmd.Parameters.AddWithValue("@current_x", (object?)input.CurrentX ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@current_y", (object?)input.CurrentY ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@current_z", (object?)input.CurrentZ ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@work_sta", input.WorkSta);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 新增机器。
    /// </summary>
    public async Task AddMachineAsync(AddMachineInput input)
    {
        const string sql = @"
INSERT INTO machine
(line_no, station_code, name, type_name, area_name, machine_no, ip, port, x, y, z, safe_z_down, safe_z_up, absolute_pos, x_dis, y_dis, z_dis, dis_shake, process_range, current_x, current_y, current_z, state)
VALUES
(@line_no, @station_code, @name, @type_name, @area_name, @machine_no, @ip, @port, @x, @y, @z, @safe_z_down, @safe_z_up, @absolute_pos, @x_dis, @y_dis, @z_dis, @dis_shake, @process_range, @current_x, @current_y, @current_z, @state);";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@line_no", input.LineNo);
        cmd.Parameters.AddWithValue("@station_code", input.StationCode ?? string.Empty);
        cmd.Parameters.AddWithValue("@name", input.Name);
        cmd.Parameters.AddWithValue("@type_name", input.TypeName);
        cmd.Parameters.AddWithValue("@area_name", input.AreaName ?? string.Empty);
        cmd.Parameters.AddWithValue("@machine_no", input.MachineNo);
        cmd.Parameters.AddWithValue("@ip", input.Ip ?? string.Empty);
        cmd.Parameters.AddWithValue("@port", input.Port);
        cmd.Parameters.AddWithValue("@x", input.X);
        cmd.Parameters.AddWithValue("@y", input.Y);
        cmd.Parameters.AddWithValue("@z", input.Z);
        cmd.Parameters.AddWithValue("@safe_z_down", input.SafeZDown);
        cmd.Parameters.AddWithValue("@safe_z_up", input.SafeZUp);
        cmd.Parameters.AddWithValue("@absolute_pos", input.AbsolutePos);
        cmd.Parameters.AddWithValue("@x_dis", input.XDis ?? "0");
        cmd.Parameters.AddWithValue("@y_dis", input.YDis ?? "0");
        cmd.Parameters.AddWithValue("@z_dis", input.ZDis ?? "0");
        cmd.Parameters.AddWithValue("@dis_shake", input.DisShake);
        cmd.Parameters.AddWithValue("@process_range", input.ProcessRange ?? string.Empty);
        cmd.Parameters.AddWithValue("@current_x", (object?)input.CurrentX ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@current_y", (object?)input.CurrentY ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@current_z", (object?)input.CurrentZ ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@state", input.State);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 新增控制器。
    /// </summary>
    public async Task AddControllerAsync(AddControllerInput input)
    {
        const string sql = @"
INSERT INTO controller
(name, type_name, ip, port, device_no, state)
VALUES
(@name, @type_name, @ip, @port, @device_no, @state);";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", input.Name);
        cmd.Parameters.AddWithValue("@type_name", input.TypeName);
        cmd.Parameters.AddWithValue("@ip", input.Ip ?? string.Empty);
        cmd.Parameters.AddWithValue("@port", input.Port);
        cmd.Parameters.AddWithValue("@device_no", input.DeviceNo);
        cmd.Parameters.AddWithValue("@state", input.State);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 新增工艺步骤。
    /// </summary>
    public async Task AddProcessStepAsync(AddProcessInput input)
    {
        const string sql = @"
INSERT INTO process_route
(process_name, step_no, step_name, execute_time, device, enabled, z_axis_back_home, safe_position, machine_no)
VALUES
(@process_name, @step_no, @step_name, @execute_time, @device, @enabled, @z_axis_back_home, @safe_position, @machine_no);";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@process_name", input.ProcessName);
        cmd.Parameters.AddWithValue("@step_no", input.StepNo);
        cmd.Parameters.AddWithValue("@step_name", input.StepName);
        cmd.Parameters.AddWithValue("@execute_time", input.ExecuteTime);
        cmd.Parameters.AddWithValue("@device", input.Device);
        cmd.Parameters.AddWithValue("@enabled", input.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@z_axis_back_home", input.ZAxisBackHome ? 1 : 0);
        cmd.Parameters.AddWithValue("@safe_position", input.SafePosition ?? string.Empty);
        cmd.Parameters.AddWithValue("@machine_no", input.MachineNo);
        await cmd.ExecuteNonQueryAsync();
    }

    
}


