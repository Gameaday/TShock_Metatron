extern alias BCryptNet;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using BC = BCryptNet::BCrypt.Net.BCrypt;

#nullable enable

namespace Metatron;

public class GatekeeperService
{
    private readonly TerrariaPlugin _plugin;
    private readonly CoreConfig _config;
    private readonly DatabaseService _db;
    private readonly DiscordService _discord;

    private readonly ConcurrentDictionary<int, DateTime> _limboPlayers = new();
    private readonly ConcurrentDictionary<string, int> _loginStrikes = new();
    
    private int _tickCounter = 0;

    public GatekeeperService(TerrariaPlugin plugin, CoreConfig config, DatabaseService db, DiscordService discord)
    {
        _plugin = plugin; _config = config; _db = db; _discord = discord;
    }

    public void EnableHooks()
    {
        TShock.Config.Settings.RequireLogin = true;
        if (!_config.EnableFrictionlessAuth) TShock.Config.Settings.DisableUUIDLogin = true;

        ServerApi.Hooks.NetGetData.Register(_plugin, OnGetData, 100);
        ServerApi.Hooks.ServerJoin.Register(_plugin, OnJoin, int.MaxValue);
        ServerApi.Hooks.NetGreetPlayer.Register(_plugin, OnGreet);
        ServerApi.Hooks.ServerLeave.Register(_plugin, OnLeave);
        ServerApi.Hooks.GameUpdate.Register(_plugin, OnPulse);

        Commands.ChatCommands.Add(new Command("", VerifyCommand, "verify"));
        Commands.ChatCommands.Add(new Command("", UnlinkCommand, "unlink"));
    }

    public void DisableHooks()
    {
        ServerApi.Hooks.NetGetData.Deregister(_plugin, OnGetData);
        ServerApi.Hooks.ServerJoin.Deregister(_plugin, OnJoin);
        ServerApi.Hooks.NetGreetPlayer.Deregister(_plugin, OnGreet);
        ServerApi.Hooks.ServerLeave.Deregister(_plugin, OnLeave);
        ServerApi.Hooks.GameUpdate.Deregister(_plugin, OnPulse);
    }

    private void OnGetData(GetDataEventArgs args)
    {
        if (args.Handled || !_config.EnableDiscordGate) return;
        var player = TShock.Players[args.Msg.whoAmI];
        if (player == null) return;

        if (_limboPlayers.ContainsKey(player.Index) && args.MsgID != PacketTypes.PasswordSend && (int)args.MsgID != 82) { args.Handled = true; return; }

        if (args.MsgID == PacketTypes.PasswordSend)
        {
            using var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length));
            string entered = reader.ReadString();
            if (_discord.PendingPins.TryRemove(entered, out var data))
            {
                if (DateTime.UtcNow > data.Expiry) return;

                if (_db.Ledger.TryGetValue(player.Name.ToLower(), out var record) && record.DiscordId != data.DiscordId && !player.IsLoggedIn)
                {
                    player.SendErrorMessage("Identity mismatch! This account is pinned to another Discord user. Log in with your recovery password first.");
                    args.Handled = true; return;
                }

                args.Handled = true; FinalizeLinkage(player, data.DiscordId);
            }
        }
    }

    private void OnJoin(JoinEventArgs args)
    {
        var player = TShock.Players[args.Who];
        if (player == null || !player.Active || player.Name == TSServerPlayer.AccountName) return;

        bool isVerified = false;
        if (!string.IsNullOrWhiteSpace(player.Name) && !string.IsNullOrWhiteSpace(player.UUID))
        {
            if (_db.Ledger.TryGetValue(player.Name.ToLower(), out var record) && record.Uuid == player.UUID)
            {
                isVerified = true;
                if (_config.EnableFrictionlessAuth) { var acc = TShock.UserAccounts.GetUserAccountByName(player.Name); if (acc != null) player.Account = acc; }
                
                // FIRE-AND-FORGET AUDIT: Ensures no lag on join, but boots them quickly if invalid.
                _ = Task.Run(async () => {
                    bool valid = await _discord.CheckUserRoleAsync(record.DiscordId);
                    if (!valid)
                    {
                        if (_db.Ledger.TryRemove(player.Name.ToLower(), out _))
                        {
                            await _db.RemoveSealAsync(record.DiscordId);
                            player.Disconnect("✨ Celestial Seal severed: You are no longer in the Discord server or lack the required role.");
                        }
                    }
                });
            }
        }

        if (!isVerified) _limboPlayers[player.Index] = DateTime.UtcNow;
        else _limboPlayers.TryRemove(player.Index, out _);
    }

    private void OnGreet(GreetPlayerEventArgs args)
    {
        var player = TShock.Players[args.Who];
        if (player == null || !player.Active) return;

        if (_limboPlayers.ContainsKey(player.Index))
        {
            player.GodMode = true; player.SetBuff(163, 360000, true); player.mute = true;
            player.SendMessage(_config.Strings.LimboMessage, Color.White);
        }
    }

    private void VerifyCommand(CommandArgs args)
    {
        if (!_limboPlayers.ContainsKey(args.Player.Index)) { args.Player.SendInfoMessage("Already verified."); return; }
        if (args.Parameters.Count == 0 || !_discord.PendingPins.TryRemove(args.Parameters[0], out var data)) { args.Player.SendErrorMessage("Invalid PIN."); return; }

        if (_db.Ledger.TryGetValue(args.Player.Name.ToLower(), out var record) && record.DiscordId != data.DiscordId && !args.Player.IsLoggedIn)
        {
            args.Player.SendErrorMessage("Identity mismatch! Log in with your recovery password first to confirm ownership.");
            return;
        }

        FinalizeLinkage(args.Player, data.DiscordId);
    }

    private void FinalizeLinkage(TSPlayer player, ulong discordId)
    {
        player.GodMode = false; 
        
        // FIX: Setting buff time to 0 safely clears it natively through TShock
        player.SetBuff(163, 0, true); 
        
        string? newPassword = null;

        if (player.Account == null)
        {
            var account = TShock.UserAccounts.GetUserAccountByName(player.Name);
            if (account == null)
            {
                newPassword = Guid.NewGuid().ToString("N").Substring(0, 10);
                account = new UserAccount(player.Name, BC.HashPassword(newPassword), player.UUID, TShock.Config.Settings.DefaultRegistrationGroupName, DateTime.UtcNow.ToString("s"), DateTime.UtcNow.ToString("s"), "");
                TShock.UserAccounts.AddUserAccount(account);
            }
            player.Account = account; 
        }

        _ = _db.SaveSealAsync(new MetatronRecord(player.Account.Name, discordId, player.UUID));
        _limboPlayers.TryRemove(player.Index, out _);
        player.mute = false; player.Heal();
        player.SendMessage(_config.Strings.VerifySuccess, Color.LimeGreen);

        _ = Task.Run(async () => { await Task.Delay(500); NetMessage.SendData(3, player.Index); NetMessage.SendData(7, player.Index); });
        _ = _discord.PostLinkSuccessAsync(discordId, player.Name);
        if (newPassword != null && _config.ShowTemporaryPasswords) _ = _discord.SendRecoveryPasswordAsync(discordId, player.Name, newPassword);
    }

    private void UnlinkCommand(CommandArgs args)
    {
        if (args.Player.Account == null || !_db.Ledger.TryGetValue(args.Player.Account.Name.ToLower(), out var record)) { args.Player.SendErrorMessage("Not linked."); return; }
        _ = _db.RemoveSealAsync(record.DiscordId); 
        args.Player.Disconnect("Seal severed.");
    }

    private void OnPulse(EventArgs args)
    {
        if (++_tickCounter < 60) return;
        _tickCounter = 0;
        if (_limboPlayers.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var kvp in _limboPlayers)
        {
            if (kvp.Value == DateTime.MinValue) continue; 
            if ((now - kvp.Value).TotalMinutes >= _config.VerificationTimeoutMinutes) { TShock.Players[kvp.Key]?.Disconnect("Verification timeout."); _limboPlayers.TryRemove(kvp.Key, out _); }
        }
    }

    private void OnLeave(LeaveEventArgs args) { _limboPlayers.TryRemove(args.Who, out _); }
}
