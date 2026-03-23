extern alias BCryptNet;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using BC = BCryptNet::BCrypt.Net.BCrypt;

#nullable enable

namespace Metatron;

public partial class MetatronPlugin
{
    private void InitializeGatekeeperEnforcement()
    {
        if (_config.EnableDiscordGate)
        {
            TShock.Config.Settings.RequireLogin = true;
            if (!_config.EnableFrictionlessAuth) TShock.Config.Settings.DisableUUIDLogin = true;
            
            TShock.Log.ConsoleInfo("[Metatron] Gatekeeper enforcement applied.");
        }
    }

    private void OnGatekeeperPassword(GetDataEventArgs args)
    {
        if (args.Handled || !_config.EnableDiscordGate || (int)args.MsgID != 38) return;

        var player = TShock.Players[args.Msg.whoAmI];
        if (player == null || player.IsLoggedIn) return; 

        using var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length));
        string enteredPassword = reader.ReadString();
        string ip = player.IP ?? "Unknown";

        if (_loginStrikes.TryGetValue(ip, out int strikes) && strikes >= 5)
        {
            player.Disconnect("⛔ Too many incorrect attempts. Please wait before trying again.");
            args.Handled = true;
            return; 
        }

        string serverPass = TShock.Config.Settings.ServerPassword;
        bool hasEventLock = !string.IsNullOrEmpty(serverPass);

        // --- SCENARIO A: DISCORD PIN ---
        if (_pendingPins.TryGetValue(enteredPassword, out var data))
        {
            if (DateTime.UtcNow > data.Expiry) 
            { 
                _pendingPins.TryRemove(enteredPassword, out _); 
                return; 
            }

            args.Handled = true; 
            _loginStrikes.TryRemove(ip, out _);

            var account = TShock.UserAccounts.GetUserAccountByName(player.Name);
            if (account == null)
            {
                string tempPass = Guid.NewGuid().ToString("N").Substring(0, 10);
                account = new UserAccount(player.Name, BC.HashPassword(tempPass), player.UUID, TShock.Config.Settings.DefaultRegistrationGroupName, DateTime.UtcNow.ToString("s"), DateTime.UtcNow.ToString("s"), "");
                TShock.UserAccounts.AddUserAccount(account);
                
                if (_config.ShowTemporaryPasswords) _pendingPasswords[player.UUID] = tempPass;
            }
            player.Account = account; 

            var record = new MetatronRecord(player.Account.Name, data.DiscordId, player.UUID);
            _ledger[player.Account.Name.ToLower()] = record;
            _ = SaveSealToDatabase(record);
            _pendingPins.TryRemove(enteredPassword, out _);

            if (hasEventLock)
            {
                player.Disconnect("✨ Discord Linked! Please reconnect and enter the Event Server Password.");
            }
            else
            {
                _limboPlayers.TryRemove(args.Msg.whoAmI, out _); // INSTANT EXTRACTION
                TShock.Log.ConsoleInfo($"[Metatron] {player.Name} verified via Discord PIN. Pushing handshake...");
                PushHandshake(player.Index);
            }
        }
        
        // --- SCENARIO B: EVENT PASSWORD ---
        else if (hasEventLock && enteredPassword == serverPass)
        {
            args.Handled = true;

            if (!_ledger.TryGetValue(player.Name.ToLower(), out var record) || record.Uuid != player.UUID)
            {
                player.Disconnect("⛔ Access Denied. You must verify via Discord PIN before using the Event Password.");
                return;
            }

            _loginStrikes.TryRemove(ip, out _);
            var account = TShock.UserAccounts.GetUserAccountByName(player.Name);
            if (account != null) player.Account = account; // ASSIGN FOR SSC

            _limboPlayers.TryRemove(args.Msg.whoAmI, out _); // INSTANT EXTRACTION

            TShock.Log.ConsoleInfo($"[Metatron] {player.Name} (Verified) bypassed the Event Lock.");
            PushHandshake(player.Index);
        }
        else
        {
            _loginStrikes.AddOrUpdate(ip, 1, (key, val) => val + 1);
        }
    }

    private void OnGatekeeperJoin(JoinEventArgs args)
    {
        var player = TShock.Players[args.Who];
        if (player == null || !player.Active || player.Name == TSServerPlayer.AccountName) return;

        bool isVerified = false;

        // 1. Core Verification Check (Frictionless Logic Streamlined)
        if (!string.IsNullOrWhiteSpace(player.Name) && !string.IsNullOrWhiteSpace(player.UUID))
        {
            if (_ledger.TryGetValue(player.Name.ToLower(), out var record) && record.Uuid == player.UUID)
            {
                isVerified = true;
                
                if (_config.EnableFrictionlessAuth)
                {
                    var account = TShock.UserAccounts.GetUserAccountByName(player.Name);
                    if (account != null) player.Account = account;
                }
            }
        }

        // 2. Limbo Enforcement
        if (_config.EnableDiscordGate && !isVerified)
        {
            _limboPlayers[player.Index] = DateTime.UtcNow;
        }
        else
        {
            // Safety catch: Ensure they aren't trapped if they passed Verification.
            _limboPlayers.TryRemove(player.Index, out _);
        }
    }

    private void OnGatekeeperGreet(GreetPlayerEventArgs args)
    {
        var player = TShock.Players[args.Who];
        if (player == null || !player.Active) return;

        // --- SMART UX ROUTER ---
        if (_limboPlayers.ContainsKey(player.Index))
        {
            player.SetBuff(163, 360000, true);
            player.mute = true;
            
            player.SendMessage($"[c/FF0000:=== DISCORD GATE ACTIVE ===]", Color.White);
            player.SendMessage($"Your TShock account is not securely linked to Discord.", Color.Yellow);
            player.SendMessage($"Type [c/00FF00:/verify <pin>] in chat to link it and unlock your character.", Color.Yellow);
            return;
        }

        // --- SEAMLESS WELCOME ---
        if (_pendingPasswords.TryRemove(player.UUID, out var pass))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                player.SendMessage($"[c/00FF00:[Metatron]] Account auto-registered successfully.", Color.White);
                player.SendMessage($"[c/00FF00:[Metatron]] Your Temporary Password is: [c/ffffff:{pass}]", Color.White);
            });
        }
        else if (player.IsLoggedIn && _config.EnableFrictionlessAuth && _ledger.ContainsKey(player.Account?.Name.ToLower() ?? ""))
        {
            player.SendSuccessMessage("✨ Celestial Seal recognized. Securely logged in.");
        }
    }

    private void VerifyCommand(CommandArgs args)
    {
        if (!_config.EnableDiscordGate) return;

        if (!_limboPlayers.ContainsKey(args.Player.Index))
        {
            args.Player.SendInfoMessage("Your account is already verified.");
            return;
        }

        if (args.Parameters.Count == 0 || !_pendingPins.TryRemove(args.Parameters[0], out var data))
        {
            args.Player.SendErrorMessage("Invalid PIN. Request a new one in Discord using !link.");
            return;
        }

        if (DateTime.UtcNow > data.Expiry)
        {
            args.Player.SendErrorMessage("Your PIN has expired. Request a new one in Discord.");
            return;
        }

        // --- THE FIX: ON-THE-FLY ACCOUNT GENERATION ---
        if (args.Player.Account == null)
        {
            var account = TShock.UserAccounts.GetUserAccountByName(args.Player.Name);
            if (account == null)
            {
                string tempPass = Guid.NewGuid().ToString("N").Substring(0, 10);
                account = new UserAccount(args.Player.Name, BC.HashPassword(tempPass), args.Player.UUID, TShock.Config.Settings.DefaultRegistrationGroupName, DateTime.UtcNow.ToString("s"), DateTime.UtcNow.ToString("s"), "");
                TShock.UserAccounts.AddUserAccount(account);
                
                // DM the temporary password later
                if (_config.ShowTemporaryPasswords) _pendingPasswords[args.Player.UUID] = tempPass;
            }
            args.Player.Account = account; 
        }

        var record = new MetatronRecord(args.Player.Account.Name, data.DiscordId, args.Player.UUID);
        _ledger[args.Player.Account.Name.ToLower()] = record;
        _ = SaveSealToDatabase(record);

        _limboPlayers.TryRemove(args.Player.Index, out _);
        args.Player.mute = false;
        args.Player.Heal();
        args.Player.SendSuccessMessage("✨ Verification Complete. Welcome to the community.");
    }

    private void UnlinkCommand(CommandArgs args)
    {
        if (args.Player.Account == null || !_ledger.ContainsKey(args.Player.Account.Name.ToLower()))
        {
            args.Player.SendErrorMessage("Your account is not linked to Discord.");
            return;
        }

        if (_ledger.TryRemove(args.Player.Account.Name.ToLower(), out var record))
        {
            _ = RemoveSealFromDatabase(record.DiscordId); 
            args.Player.SendSuccessMessage("✨ Your Celestial Seal has been severed. You are no longer linked.");

            if (_config.EnableDiscordGate)
            {
                _limboPlayers[args.Player.Index] = DateTime.UtcNow;
                args.Player.SetBuff(163, 360000, true);
                args.Player.mute = true;
                args.Player.SendMessage($"[c/FF0000:=== SERVER IS DISCORD-GATED ===]", Color.White);
                args.Player.SendMessage($"Type [c/00FF00:/verify <pin>] to re-enter.", Color.White);
            }
        }
    }

    private void PushHandshake(int playerIndex)
    {
        _ = Task.Run(async () => {
            await Task.Delay(500);
            NetMessage.SendData(3, playerIndex); 
            NetMessage.SendData(7, playerIndex); 
        });
    }

    private Task SaveSealToDatabase(MetatronRecord record)
    {
        return Task.Run(async () => {
            await _dbLock.WaitAsync();
            try {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO Ledger VALUES (@n, @d, @u)";
                cmd.Parameters.AddWithValue("@n", record.AccountName.ToLower());
                cmd.Parameters.AddWithValue("@d", record.DiscordId.ToString());
                cmd.Parameters.AddWithValue("@u", record.Uuid);
                cmd.ExecuteNonQuery();
            } catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] DB Save Error: {ex.Message}"); }
            finally { _dbLock.Release(); }
        });
    }

    private Task RemoveSealFromDatabase(ulong discordId)
    {
        return Task.Run(async () => {
            await _dbLock.WaitAsync();
            try {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Ledger WHERE DiscordId = @did";
                cmd.Parameters.AddWithValue("@did", discordId.ToString());
                cmd.ExecuteNonQuery();
            } catch { }
            finally { _dbLock.Release(); }
        });
    }

    private void OnGatekeeperLeave(LeaveEventArgs args) 
    { 
        _limboPlayers.TryRemove(args.Who, out _); 
        _pendingPasswords.TryRemove(TShock.Players[args.Who]?.UUID ?? "", out _);
    }
}