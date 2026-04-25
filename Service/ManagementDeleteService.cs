using MySql.Data.MySqlClient;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 四个管理页面的删除服务（DELETE 操作）。
/// </summary>
public sealed class ManagementDeleteService
{
    private readonly string _connectionString;

    public ManagementDeleteService(string connectionString)
    {
        _connectionString = connectionString;
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