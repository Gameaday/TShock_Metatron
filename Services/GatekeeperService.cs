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

#nullable enable

namespace Metatron;

public class GatekeeperService
{
    private readonly TerrariaPlugin _plugin;
    private readonly CoreConfig _config;
    private readonly DatabaseService _db;
    private readonly DiscordService _discord;

    private readonly ConcurrentDictionary<int, DateTime> _limboPlayers = new();
    private readonly ConcurrentDictionary<string, string> _pendingPasswords = new();
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

        if (args.MsgID == PacketTypes.PasswordSend && !player.IsLoggedIn)
        {
            using var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length));
            string enteredPassword = reader.ReadString();
            string ip = player.IP ?? "Unknown";

            if (_loginStrikes.TryGetValue(ip, out int strikes) && strikes >= 5)
            {
                player.Disconnect("⛔ Too many incorrect attempts. Please wait before trying again.");
                args.Handled = true;
                return;
            }

            if (_discord.PendingPins.TryRemove(enteredPassword, out var data))
            {
                if (DateTime.UtcNow > data.Expiry) return; 

                args.Handled = true; 
                _loginStrikes.TryRemove(ip, out _);

                FinalizeLinkage(player, data.DiscordId);
            }
            else
            {
                _loginStrikes.AddOrUpdate(ip, 1, (key, val) => val + 1);
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
            player.SetBuff(163, 360000, true); 
            player.mute = true;
            player.SendMessage($"[c/FF0000:=== DISCORD GATE ACTIVE ===]", Color.White);
            player.SendMessage($"Your TShock account is not securely linked to Discord.", Color.Yellow);
            player.SendMessage($"Type [c/00FF00:/verify <pin>] in chat to link it and unlock your character.", Color.Yellow);
            return;
        }

        if (_pendingPasswords.TryRemove(player.UUID, out var pass))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                player.SendMessage($"[c/00FF00:[Metatron]] Account auto-registered successfully.", Color.White);
                player.SendMessage($"[c/00FF00:[Metatron]] Your Temporary Password is: [c/ffffff:{pass}]", Color.White);
            });
        }
        else if (player.IsLoggedIn && _config.EnableFrictionlessAuth)
        {
            player.SendSuccessMessage("✨ Celestial Seal recognized. Securely logged in.");
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

        FinalizeLinkage(args.Player, data.DiscordId);
    }

    private void FinalizeLinkage(TSPlayer player, ulong discordId)
    {
        if (player.Account == null)
        {
            var account = TShock.UserAccounts.GetUserAccountByName(player.Name);
            if (account == null)
            {
                string tempPass = Guid.NewGuid().ToString("N").Substring(0, 10);
                
                // FIX: Use TShock's native, internal BCrypt library directly.
                string hashedPassword = BCrypt.Net.BCrypt.HashPassword(tempPass);
                
                account = new UserAccount(player.Name, hashedPassword, player.UUID, TShock.Config.Settings.DefaultRegistrationGroupName, DateTime.UtcNow.ToString("s"), DateTime.UtcNow.ToString("s"), "");
                TShock.UserAccounts.AddUserAccount(account);
                
                if (_config.ShowTemporaryPasswords) _pendingPasswords[player.UUID] = tempPass;
            }
            player.Account = account; 
        }

        var record = new MetatronRecord(player.Account.Name, discordId, player.UUID);
        _ = _db.SaveSealAsync(record);

        _limboPlayers.TryRemove(player.Index, out _);
        player.mute = false;
        player.Heal();
        player.SendSuccessMessage("✨ Verification Complete. Welcome to the community.");

        _ = Task.Run(async () => {
            await Task.Delay(500);
            NetMessage.SendData(3, player.Index); 
            NetMessage.SendData(7, player.Index); 
        });

        _ = _discord.PostLinkSuccessAsync(discordId, player.Name);
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
        _pendingPasswords.TryRemove(TShock.Players[args.Who]?.UUID ?? "", out _);
    }
}
