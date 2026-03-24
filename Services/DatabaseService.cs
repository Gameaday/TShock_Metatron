using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TShockAPI;

#nullable enable

namespace Metatron;

public partial class MetatronPlugin
{
    private string BasePath => Path.Combine(TShock.SavePath, "Metatron");
    private string DbPath => Path.Combine(BasePath, "Archive.sqlite");
    private string CoreConfigPath => Path.Combine(BasePath, "Core.json");

    private FileSystemWatcher? _configWatcher;
    private DateTime _lastConfigReload = DateTime.UtcNow;

    private void InitializePersistence()
    {
        Directory.CreateDirectory(BasePath);
        LoadCoreConfig();
        InitializeArchive();
        StartHotReloader();
    }

    private void StartHotReloader()
    {
        try
        {
            _configWatcher?.Dispose();
            _configWatcher = new FileSystemWatcher(BasePath, "Core.json")
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _configWatcher.Changed += (s, e) => {
                if ((DateTime.UtcNow - _lastConfigReload).TotalSeconds > 1.5) {
                    _lastConfigReload = DateTime.UtcNow;
                    Task.Delay(500).ContinueWith(_ => LoadCoreConfig());
                }
            };
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] FileWatcher failed: {ex.Message}"); }
    }

    private void LoadCoreConfig()
    {
        try
        {
            if (File.Exists(CoreConfigPath))
            {
                var tempConfig = JsonSerializer.Deserialize(File.ReadAllText(CoreConfigPath), MetatronJsonContext.Default.CoreConfig);
                if (tempConfig != null) _config = tempConfig; 
            }
            else File.WriteAllText(CoreConfigPath, JsonSerializer.Serialize(_config, MetatronJsonContext.Default.CoreConfig));
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Core Config Error: {ex.Message}"); }
    }

    private void InitializeArchive()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS Ledger (AccountName TEXT PRIMARY KEY, DiscordId TEXT, Uuid TEXT);";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT AccountName, DiscordId, Uuid FROM Ledger";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var record = new MetatronRecord(reader.GetString(0), ulong.Parse(reader.GetString(1)), reader.GetString(2));
                _ledger.TryAdd(record.AccountName.ToLower(), record);
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] DB Error: {ex.Message}"); }
    }
}
