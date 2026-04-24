using MySql.Data.MySqlClient;

namespace AutomaticOnlineHostComputer.Infrastructure.Data;

/// <summary>
/// 四个管理页面的新增写入服务。
/// 说明：
/// 1) 统一封装 INSERT 逻辑，避免散落在按钮事件中。
/// 2) 通过输入模型解耦 UI 与数据访问层。
/// </summary>
public sealed class ManagementCommandService
{
    private readonly string _connectionString;

    public ManagementCommandService(string connectionString)
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
(line_no, crane_no, name, ip, port, encoder_ip, encoder_port, encoder_no, origin_x, origin_y, origin_z, width, abs_x_offset, ratio_x, ratio_y, ratio_z, start_x, end_x, limit_zp, limit_zn, pulse_x, icon_path, work_sta)
VALUES
(@line_no, @crane_no, @name, @ip, @port, @encoder_ip, @encoder_port, @encoder_no, @origin_x, @origin_y, @origin_z, @width, @abs_x_offset, @ratio_x, @ratio_y, @ratio_z, @start_x, @end_x, @limit_zp, @limit_zn, @pulse_x, @icon_path, @work_sta);";

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
        cmd.Parameters.AddWithValue("@icon_path", input.IconPath ?? string.Empty);
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
(line_no, station_code, name, type_name, area_name, machine_no, ip, port, x, y, z, safe_z_down, safe_z_up, absolute_pos, x_dis, y_dis, z_dis, dis_shake, process_range, icon_path, state)
VALUES
(@line_no, @station_code, @name, @type_name, @area_name, @machine_no, @ip, @port, @x, @y, @z, @safe_z_down, @safe_z_up, @absolute_pos, @x_dis, @y_dis, @z_dis, @dis_shake, @process_range, @icon_path, @state);";

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
        cmd.Parameters.AddWithValue("@icon_path", input.IconPath ?? string.Empty);
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
(name, type_name, ip, port, device_no, icon_path, state)
VALUES
(@name, @type_name, @ip, @port, @device_no, @icon_path, @state);";

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
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

    /// <summary>
    /// 删除天车（按主键）。
    /// </summary>
    public Task DeleteCraneAsync(int id) => ExecuteDeleteAsync("DELETE FROM crane WHERE id=@id;", id);

    /// <summary>
    /// 删除机器（按主键）。
    /// </summary>
    public Task DeleteMachineAsync(int id) => ExecuteDeleteAsync("DELETE FROM machine WHERE id=@id;", id);

    /// <summary>
    /// 删除控制器（按主键）。
    /// </summary>
    public Task DeleteControllerAsync(int id) => ExecuteDeleteAsync("DELETE FROM controller WHERE id=@id;", id);

    /// <summary>
    /// 删除工艺步骤（按主键）。
    /// </summary>
    public Task DeleteProcessStepAsync(int id) => ExecuteDeleteAsync("DELETE FROM process_route WHERE id=@id;", id);

    /// <summary>
    /// 通用删除执行器。
    /// </summary>
    private async Task ExecuteDeleteAsync(string sql, int id)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>新增天车输入模型。</summary>
public sealed class AddCraneInput
{
    public int LineNo { get; set; }
    public int CraneNo { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public string EncoderIp { get; set; } = string.Empty;
    public int EncoderPort { get; set; }
    public int EncoderNo { get; set; }
    public long OriginX { get; set; }
    public long OriginY { get; set; }
    public long OriginZ { get; set; }
    public long Width { get; set; }
    public int AbsXOffset { get; set; }
    public double RatioX { get; set; }
    public double RatioY { get; set; }
    public double RatioZ { get; set; }
    public long StartX { get; set; }
    public long EndX { get; set; }
    public long LimitZP { get; set; }
    public long LimitZN { get; set; }
    public double PulseX { get; set; }
    public string? IconPath { get; set; }
    public int WorkSta { get; set; }
}

/// <summary>新增机器输入模型。</summary>
public sealed class AddMachineInput
{
    public int LineNo { get; set; }
    public string? StationCode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? AreaName { get; set; }
    public int MachineNo { get; set; }
    public string? Ip { get; set; }
    public int Port { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int SafeZDown { get; set; }
    public int SafeZUp { get; set; }
    public int AbsolutePos { get; set; }
    public string? XDis { get; set; }
    public string? YDis { get; set; }
    public string? ZDis { get; set; }
    public int DisShake { get; set; }
    public string? ProcessRange { get; set; }
    public string? IconPath { get; set; }
    public int State { get; set; }
}

/// <summary>新增控制器输入模型。</summary>
public sealed class AddControllerInput
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? Ip { get; set; }
    public int Port { get; set; }
    public int DeviceNo { get; set; }
    public string? IconPath { get; set; }
    public int State { get; set; }
}

/// <summary>新增工艺输入模型。</summary>
public sealed class AddProcessInput
{
    public string ProcessName { get; set; } = string.Empty;
    public int StepNo { get; set; }
    public string StepName { get; set; } = string.Empty;
    public int ExecuteTime { get; set; }
    public string Device { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool ZAxisBackHome { get; set; }
    public string? SafePosition { get; set; }
    public int MachineNo { get; set; }
}
