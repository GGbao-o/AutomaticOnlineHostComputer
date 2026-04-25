using AutomaticOnlineHostComputer.Domain.Models;
using MySql.Data.MySqlClient;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 四个管理页面的更新服务（UPDATE 操作）。
/// 通过输入模型解耦 UI 与数据访问层。
/// </summary>
public sealed class ManagementUpdateService
{
    private readonly string _connectionString;

    public ManagementUpdateService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// 更新天车。
    /// </summary>
    public async Task UpdateCraneAsync(int id, AddCraneInput input)
    {
        const string sql = @"
UPDATE crane SET
line_no=@line_no, crane_no=@crane_no, name=@name, ip=@ip, port=@port,
encoder_ip=@encoder_ip, encoder_port=@encoder_port, encoder_no=@encoder_no,
origin_x=@origin_x, origin_y=@origin_y, origin_z=@origin_z, width=@width,
abs_x_offset=@abs_x_offset, ratio_x=@ratio_x, ratio_y=@ratio_y, ratio_z=@ratio_z,
start_x=@start_x, end_x=@end_x, limit_zp=@limit_zp, limit_zn=@limit_zn, pulse_x=@pulse_x,
icon_path=@icon_path, work_sta=@work_sta
WHERE id=@id;";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
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
        cmd.Parameters.AddWithValue("@icon_path", input.IconPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@work_sta", input.WorkSta);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 更新机器。
    /// </summary>
    public async Task UpdateMachineAsync(int id, AddMachineInput input)
    {
        const string sql = @"
UPDATE machine SET
line_no=@line_no, station_code=@station_code, name=@name, type_name=@type_name, area_name=@area_name,
machine_no=@machine_no, ip=@ip, port=@port, x=@x, y=@y, z=@z,
safe_z_down=@safe_z_down, safe_z_up=@safe_z_up, absolute_pos=@absolute_pos,
x_dis=@x_dis, y_dis=@y_dis, z_dis=@z_dis, dis_shake=@dis_shake, process_range=@process_range,
icon_path=@icon_path, state=@state
WHERE id=@id;";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
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
        cmd.Parameters.AddWithValue("@icon_path", input.IconPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@state", input.State);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 更新控制器。
    /// </summary>
    public async Task UpdateControllerAsync(int id, AddControllerInput input)
    {
        const string sql = @"
UPDATE controller SET
name=@name, type_name=@type_name, ip=@ip, port=@port, device_no=@device_no,
icon_path=@icon_path, state=@state
WHERE id=@id;";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", input.Name);
        cmd.Parameters.AddWithValue("@type_name", input.TypeName);
        cmd.Parameters.AddWithValue("@ip", input.Ip ?? string.Empty);
        cmd.Parameters.AddWithValue("@port", input.Port);
        cmd.Parameters.AddWithValue("@device_no", input.DeviceNo);
        cmd.Parameters.AddWithValue("@icon_path", input.IconPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@state", input.State);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 更新工艺步骤。
    /// </summary>
    public async Task UpdateProcessStepAsync(int id, AddProcessInput input)
    {
        const string sql = @"
UPDATE process_route SET
process_name=@process_name, step_no=@step_no, step_name=@step_name,
execute_time=@execute_time, device=@device, enabled=@enabled,
z_axis_back_home=@z_axis_back_home, safe_position=@safe_position, machine_no=@machine_no
WHERE id=@id;";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
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