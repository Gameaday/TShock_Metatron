using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TShockAPI;

#nullable enable

namespace Metatron;

public class DatabaseService
{
    private readonly string _dbConnectionString;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    
    public ConcurrentDictionary<string, MetatronRecord> Ledger { get; } = new();

    public DatabaseService()
    {
        string path = Path.Combine(TShock.SavePath, "Metatron", "Archive.sqlite");
        _dbConnectionString = $"Data Source={path};Pooling=True;";
    }

    public void Initialize()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(TShock.SavePath, "Metatron"))!);
            using var conn = new SqliteConnection(_dbConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS Ledger (AccountName TEXT PRIMARY KEY, DiscordId TEXT, Uuid TEXT);";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT AccountName, DiscordId, Uuid FROM Ledger";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var record = new MetatronRecord(reader.GetString(0), ulong.Parse(reader.GetString(1)), reader.GetString(2));
                Ledger.TryAdd(record.AccountName.ToLower(), record);
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] DB Error: {ex.Message}"); }
    }

    public async Task SaveSealAsync(MetatronRecord record)
    {
        Ledger[record.AccountName.ToLower()] = record;
        await _dbLock.WaitAsync();
        try {
            using var conn = new SqliteConnection(_dbConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO Ledger VALUES (@n, @d, @u)";
            cmd.Parameters.AddWithValue("@n", record.AccountName.ToLower());
            cmd.Parameters.AddWithValue("@d", record.DiscordId.ToString());
            cmd.Parameters.AddWithValue("@u", record.Uuid);
            
            // NOW TRULY ASYNC
            await cmd.ExecuteNonQueryAsync();
        } catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] DB Save Error: {ex.Message}"); }
        finally { _dbLock.Release(); }
    }

    public async Task<List<string>> RemoveSealAsync(ulong discordId)
    {
        var removedAccounts = new List<string>();

        // Remove from in-memory ledger first
        foreach (var kvp in Ledger)
        {
            if (kvp.Value.DiscordId == discordId)
            {
                if (Ledger.TryRemove(kvp.Key, out _))
                {
                    removedAccounts.Add(kvp.Key);
                }
            }
        }

        await _dbLock.WaitAsync();
        try {
            using var conn = new SqliteConnection(_dbConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Ledger WHERE DiscordId = @did";
            cmd.Parameters.AddWithValue("@did", discordId.ToString());
            
            // NOW TRULY ASYNC
            await cmd.ExecuteNonQueryAsync();
        } catch { }
        finally { _dbLock.Release(); }

        return removedAccounts;
    }
}
