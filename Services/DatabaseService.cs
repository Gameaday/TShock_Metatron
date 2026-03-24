using System;
using System.Collections.Generic;
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
    private string BroadcastsPath => Path.Combine(BasePath, "Broadcasts");
    private string DbPath => Path.Combine(BasePath, "Archive.sqlite");
    private string CoreConfigPath => Path.Combine(BasePath, "Core.json");

    private FileSystemWatcher? _configWatcher;
    private DateTime _lastConfigReload = DateTime.UtcNow;

    private void InitializePersistence()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(BroadcastsPath);

        LoadCoreConfig();
        LoadBroadcastLibrary();
        InitializeArchive();
        StartHotReloader();
    }

    private void StartHotReloader()
    {
        try
        {
            _configWatcher?.Dispose();
            _configWatcher = new FileSystemWatcher(BasePath)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = true
            };
            _configWatcher.Changed += OnFileChanged;
            _configWatcher.Created += OnFileChanged;
            _configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] FileWatcher failed: {ex.Message}"); }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if ((DateTime.UtcNow - _lastConfigReload).TotalSeconds > 1.5)
        {
            _lastConfigReload = DateTime.UtcNow;
            if (e.FullPath.Contains("Core.json")) Task.Delay(500).ContinueWith(_ => LoadCoreConfig());
            else if (e.FullPath.Contains("Broadcasts")) Task.Delay(500).ContinueWith(_ => LoadBroadcastLibrary());
        }
    }

    private void LoadCoreConfig()
    {
        try
        {
            if (File.Exists(CoreConfigPath))
            {
                // Safe-Swap: Only overwrite in-memory config if the JSON is valid
                var tempConfig = JsonSerializer.Deserialize(File.ReadAllText(CoreConfigPath), MetatronJsonContext.Default.CoreConfig);
                if (tempConfig != null) _config = tempConfig; 
            }
            else
            {
                File.WriteAllText(CoreConfigPath, JsonSerializer.Serialize(_config, MetatronJsonContext.Default.CoreConfig));
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Core Config Error (Kept old config safely): {ex.Message}"); }
    }

    private void LoadBroadcastLibrary()
    {
        var safeTempList = new List<Broadcast>();
        try
        {
            var files = Directory.GetFiles(BroadcastsPath, "*.json");

            if (files.Length == 0)
            {
                string defaultPath = Path.Combine(BroadcastsPath, "Welcome.json");
                var defaultList = new List<Broadcast> {
                    new Broadcast { Name = "Welcome Example", TriggerTypes = new() { "Join" }, Enabled = true, Messages = new() { "Welcome {player} to the server!" } }
                };
                File.WriteAllText(defaultPath, JsonSerializer.Serialize(defaultList, MetatronJsonContext.Default.ListBroadcast));
                files = new[] { defaultPath };
            }

            foreach (var file in files)
            {
                try
                {
                    var list = JsonSerializer.Deserialize(File.ReadAllText(file), MetatronJsonContext.Default.ListBroadcast);
                    if (list != null) safeTempList.AddRange(list);
                }
                catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Failed to load {Path.GetFileName(file)}: {ex.Message}"); }
            }

            // Safe-Swap
            _allBroadcasts = safeTempList;
            TShock.Log.ConsoleInfo($"[Metatron] Scribe has indexed {_allBroadcasts.Count} broadcast triggers.");
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Broadcast Library Error: {ex.Message}"); }
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
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Database initialization failed: {ex.Message}"); }
    }
}
