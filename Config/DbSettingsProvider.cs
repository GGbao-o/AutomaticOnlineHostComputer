using System.IO;
using System.Text.Json;

namespace AutomaticOnlineHostComputer.Infrastructure.Config;

/// <summary>
/// 数据库配置读取器。
/// 说明：优先读取程序目录下的 dbsettings.json，避免在代码中写死连接信息。
/// </summary>
public static class DbSettingsProvider
{
    /// <summary>
    /// 读取并构建 MySQL 连接字符串。
    /// </summary>
    public static string GetConnectionString()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(baseDir, "dbsettings.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"未找到数据库配置文件: {path}");
        }

        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<DbSettings>(json)
                       ?? throw new InvalidOperationException("dbsettings.json 反序列化失败。");

        return $"Server={settings.Host};Port={settings.Port};Database={settings.Database};Uid={settings.User};Pwd={settings.Password};CharSet=utf8mb4;";
    }

   private sealed class DbSettings
   {
       public string Host { get; set; } = "172.31.59.203";
       public int Port { get; set; } = 3307;
       public string Database { get; set; } = "automatic_online_host";
       public string User { get; set; } = "root";
       public string Password { get; set; } = string.Empty;
   }
}
