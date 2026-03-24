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
        _plugin = plugin;
        _config = config;
        _db = db;
        _discord = discord;
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
        
        TShock.Log.ConsoleInfo("[Metatron] Ironclad Gatekeeper enforcement applied.");
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

        if (_limboPlayers.ContainsKey(player.Index))
        {
            if (args.MsgID != PacketTypes.PasswordSend && (int)args.MsgID != 82)
            {
                args.Handled = true; 
                return;
            }
        }

        if (args.MsgID == PacketTypes.PasswordSend)
        {
            using var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length));
            string enteredPassword = reader.ReadString();
            string ip = player.IP ?? "Unknown";

            if (_discord.PendingPins.TryRemove(enteredPassword, out var data))
            {
                if (DateTime.UtcNow > data.Expiry) return; 

                bool accountExists = TShock.UserAccounts.GetUserAccountByName(player.Name) != null;

                if (accountExists && !player.IsLoggedIn)
                {
                    player.SendErrorMessage("This account is already registered. Please log in with your TShock password first, then use /verify <pin>.");
                    args.Handled = true;
                    return;
                }

                args.Handled = true; 
                _loginStrikes.TryRemove(ip, out _);
                FinalizeLinkage(player, data.DiscordId);
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
                if (_config.EnableFrictionlessAuth)
                {
                    var account = TShock.UserAccounts.GetUserAccountByName(player.Name);
                    if (account != null) player.Account = account;
                }
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
            player.GodMode = true; 
            player.SetBuff(163, 360000, true); 
            player.mute = true;

            bool accountExists = TShock.UserAccounts.GetUserAccountByName(player.Name) != null;

            if (accountExists)
            {
                player.SendMessage($"[c/FF0000:=== DISCORD GATE ACTIVE ===]", Color.White);
                player.SendMessage($"Welcome back! Your account needs to be securely linked to Discord.", Color.Yellow);
                player.SendMessage($"1. Log in using: [c/00FF00:/login <password>]", Color.White);
                player.SendMessage($"2. Link using: [c/00FF00:/verify <discord-pin>]", Color.White);
            }
            else
            {
                player.SendMessage($"[c/FF0000:=== DISCORD GATE ACTIVE ===]", Color.White);
                player.SendMessage($"Your TShock account is not securely linked to Discord.", Color.Yellow);
                player.SendMessage($"Type your Discord PIN into the Server Password box, or use [c/00FF00:/verify <pin>].", Color.White);
            }
        }
    }

    private void VerifyCommand(CommandArgs args)
    {
        if (!_limboPlayers.ContainsKey(args.Player.Index))
        {
            args.Player.SendInfoMessage("Your account is already verified.");
            return;
        }

        if (args.Parameters.Count == 0 || !_discord.PendingPins.TryRemove(args.Parameters[0], out var data))
        {
            args.Player.SendErrorMessage("Invalid PIN. Request a new one in Discord using !link.");
            return;
        }

        if (DateTime.UtcNow > data.Expiry)
        {
            args.Player.SendErrorMessage("Your PIN has expired. Request a new one in Discord.");
            return;
        }

        bool accountExists = TShock.UserAccounts.GetUserAccountByName(args.Player.Name) != null;

        if (accountExists && !args.Player.IsLoggedIn)
        {
            args.Player.SendErrorMessage("This account already exists. Please /login with your password before verifying.");
            return;
        }

        FinalizeLinkage(args.Player, data.DiscordId);
    }

    private void FinalizeLinkage(TSPlayer player, ulong discordId)
    {
        player.GodMode = false;
        player.DelBuff(163); 
        
        string? newPassword = null; // Track if a new password was generated

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

        var record = new MetatronRecord(player.Account.Name, discordId, player.UUID);
        _ = _db.SaveSealAsync(record);

        _limboPlayers.TryRemove(player.Index, out _);
        player.mute = false;
        player.Heal();
        player.SendMessage(_config.Strings.VerifySuccess, Color.LimeGreen);

        _ = Task.Run(async () => {
            await Task.Delay(500);
            NetMessage.SendData(3, player.Index); 
            NetMessage.SendData(7, player.Index); 
        });

        _ = _discord.PostLinkSuccessAsync(discordId, player.Name);

        // TRIGGER THE RECOVERY DM
        if (newPassword != null && _config.ShowTemporaryPasswords)
        {
            _ = _discord.SendRecoveryPasswordAsync(discordId, player.Name, newPassword);
        }
    }

    private void UnlinkCommand(CommandArgs args)
    {
        if (args.Player.Account == null || !_db.Ledger.ContainsKey(args.Player.Account.Name.ToLower()))
        {
            args.Player.SendErrorMessage("Your account is not linked to Discord.");
            return;
        }

        if (_db.Ledger.TryGetValue(args.Player.Account.Name.ToLower(), out var record))
        {
            _ = _db.RemoveSealAsync(record.DiscordId); 
            args.Player.Disconnect("✨ Your Celestial Seal has been severed. You are no longer linked.");
        }
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

            if ((now - kvp.Value).TotalMinutes >= _config.VerificationTimeoutMinutes)
            {
                var player = TShock.Players[kvp.Key];
                if (player != null && player.Active)
                {
                    player.Disconnect($"Verification timed out. You have {_config.VerificationTimeoutMinutes} minutes to link via Discord.");
                }
                _limboPlayers.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void OnLeave(LeaveEventArgs args) 
    { 
        _limboPlayers.TryRemove(args.Who, out _); 
    }
}
