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
    private readonly ConcurrentDictionary<string, (int Strikes, DateTime FirstStrike)> _verifyStrikes = new();
    private readonly ConcurrentDictionary<string, (int Attempts, DateTime FirstAttempt)> _joinRateLimit = new();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    
    private int _tickCounter = 0;

    public GatekeeperService(TerrariaPlugin plugin, CoreConfig config, DatabaseService db, DiscordService discord)
    {
        _plugin = plugin; _config = config; _db = db; _discord = discord;
    }

    public void EnableHooks()
    {
        TShock.Config.Settings.RequireLogin = true;
        TShock.Config.Settings.DisableUUIDLogin = true; // Always disable native UUID login

        ServerApi.Hooks.NetGetData.Register(_plugin, OnGetData, 100);
        ServerApi.Hooks.ServerJoin.Register(_plugin, OnJoin, int.MaxValue);
        ServerApi.Hooks.NetGreetPlayer.Register(_plugin, OnGreet);
        ServerApi.Hooks.ServerLeave.Register(_plugin, OnLeave);
        ServerApi.Hooks.GameUpdate.Register(_plugin, OnPulse);

        Commands.ChatCommands.Add(new Command("", VerifyCommand, "verify"));
        Commands.ChatCommands.Add(new Command("", UnlinkCommand, "unlink"));

        _discord.KickRequested += OnKickRequested;
    }

    public void DisableHooks()
    {
        _discord.KickRequested -= OnKickRequested;
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

            bool isPinGuess = entered.Length == 6 && entered.All(char.IsDigit);
            string ip = player.IP;
            var now = DateTime.UtcNow;

            if (isPinGuess)
            {
                var strikeData = _verifyStrikes.GetOrAdd(ip, (0, now));
                if ((now - strikeData.FirstStrike).TotalMinutes > 15)
                {
                    strikeData = (0, now);
                    _verifyStrikes[ip] = strikeData;
                }

                if (strikeData.Strikes >= 5)
                {
                    SafeDisconnect(player, "Disconnected: Too many invalid PIN attempts. Please wait 15 minutes before trying again.");
                    args.Handled = true;
                    return;
                }
            }

            if (_discord.PendingPins.TryGetValue(entered, out var data))
            {
                if (DateTime.UtcNow > data.Expiry)
                {
                    _discord.PendingPins.TryRemove(entered, out _);

                    if (isPinGuess)
                    {
                        var currentStrikeData = _verifyStrikes.GetOrAdd(ip, (0, now));
                        var newStrikes = currentStrikeData.Strikes + 1;
                        _verifyStrikes[ip] = (newStrikes, currentStrikeData.FirstStrike);

                        if (newStrikes >= 5)
                        {
                            SafeDisconnect(player, "Disconnected: Too many invalid PIN attempts. Please wait 15 minutes before trying again.");
                            args.Handled = true;
                            return;
                        }
                        player.SendErrorMessage($"Invalid PIN. Attempts remaining: {5 - newStrikes}");
                    }
                    args.Handled = true;
                    return;
                }

                if (_db.Ledger.TryGetValue(player.Name.ToLower(), out var record))
                {
                    if (record.DiscordId != data.DiscordId && !player.IsLoggedIn)
                    {
                        player.SendErrorMessage("Identity mismatch! This account is pinned to another Discord user. Log in with your recovery password first.");
                        args.Handled = true; return;
                    }
                }
                else if (TShock.UserAccounts.GetUserAccountByName(player.Name) != null && !player.IsLoggedIn)
                {
                    player.SendErrorMessage("This account already exists. Log in with your password first before linking to Discord.");
                    args.Handled = true; return;
                }

                _verifyStrikes.TryRemove(ip, out _);
                _discord.PendingPins.TryRemove(entered, out _);
                args.Handled = true;

                int pIndex = player.Index;
                string pName = player.Name;
                string pUuid = player.UUID;
                var pAccount = player.Account;

                _ = Task.Run(() => FinalizeLinkage(pIndex, pName, pUuid, pAccount, data.DiscordId));
            }
            else if (isPinGuess)
            {
                var currentStrikeData = _verifyStrikes.GetOrAdd(ip, (0, now));
                var newStrikes = currentStrikeData.Strikes + 1;
                _verifyStrikes[ip] = (newStrikes, currentStrikeData.FirstStrike);

                if (newStrikes >= 5)
                {
                    SafeDisconnect(player, "Disconnected: Too many invalid PIN attempts. Please wait 15 minutes before trying again.");
                    args.Handled = true;
                    return;
                }
                player.SendErrorMessage($"Invalid PIN. Attempts remaining: {5 - newStrikes}");
                args.Handled = true;
            }
            else
            {
                if (_limboPlayers.ContainsKey(player.Index))
                {
                    args.Handled = true;
                }
            }
        }
    }

    private void OnJoin(JoinEventArgs args)
    {
        var player = TShock.Players[args.Who];
        if (player == null || !player.Active || player.Name == TSServerPlayer.AccountName) return;

        // Place in limbo immediately
        _limboPlayers[args.Who] = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(player.Name) || string.IsNullOrWhiteSpace(player.UUID)) return;

        string pName = player.Name;
        string pUuid = player.UUID;
        int pIndex = args.Who;
        string ip = player.IP;

        var now = DateTime.UtcNow;
        var rateData = _joinRateLimit.GetOrAdd(ip, (0, now));
        if ((now - rateData.FirstAttempt).TotalMinutes > 1)
        {
            rateData = (0, now);
            _joinRateLimit[ip] = rateData;
        }

        var newAttempts = rateData.Attempts + 1;
        _joinRateLimit[ip] = (newAttempts, rateData.FirstAttempt);

        if (newAttempts > 5)
        {
            SafeDisconnect(player, "Disconnected: Too many login attempts. Please wait a moment before trying again.");
            return;
        }

        _ = Task.Run(async () =>
        {
            if (_db.Ledger.TryGetValue(pName.ToLower(), out var record))
            {
                bool isHashedUuid = record.Uuid.StartsWith("$2", StringComparison.Ordinal);
                bool uuidMatch = false;
                if (isHashedUuid)
                {
                    try { uuidMatch = BC.Verify(pUuid, record.Uuid); }
                    catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] BCrypt verify failed for '{pName}' (malformed hash?): {ex.Message}"); }
                }
                else
                {
                    uuidMatch = record.Uuid == pUuid;
                }

                if (uuidMatch)
                {
                    // Asynchronous upgrade path for legacy plaintext UUIDs
                    string? newlyHashedUuid = null;
                    if (!isHashedUuid)
                    {
                        newlyHashedUuid = BC.HashPassword(pUuid);
                    }

                    _mainThreadActions.Enqueue(() =>
                    {
                        var onlinePlayer = TShock.Players[pIndex];
                        // TOCTOU check: Ensure player slot hasn't been recycled
                        if (onlinePlayer != null && onlinePlayer.Active && onlinePlayer.Name == pName && onlinePlayer.UUID == pUuid)
                        {
                            _limboPlayers.TryRemove(pIndex, out _);

                            if (newlyHashedUuid != null)
                            {
                                _ = _db.SaveSealAsync(new MetatronRecord(record.AccountName, record.DiscordId, newlyHashedUuid));
                            }

                            if (_config.EnableFrictionlessAuth) { var acc = TShock.UserAccounts.GetUserAccountByName(pName); if (acc != null) onlinePlayer.Account = acc; }

                            // FIRE-AND-FORGET AUDIT: Ensures no lag on join, but boots them quickly if invalid.
                            _ = Task.Run(async () => {
                                bool valid = await _discord.CheckUserRoleAsync(record.DiscordId);
                                if (!valid)
                                {
                                    if (_db.Ledger.TryRemove(pName.ToLower(), out _))
                                    {
                                        await _db.RemoveSealAsync(record.DiscordId);
                                        _mainThreadActions.Enqueue(() =>
                                        {
                                            // TOCTOU check again
                                            var currentPlayer = TShock.Players[pIndex];
                                            if (currentPlayer != null && currentPlayer.Active && currentPlayer.Name == pName && currentPlayer.UUID == pUuid)
                                            {
                                                currentPlayer.Disconnect("✨ Celestial Seal severed: You are no longer in the Discord server or lack the required role.");
                                            }
                                        });
                                    }
                                }
                            });
                        }
                    });
                }
            }
        });
    }

    private void OnGreet(GreetPlayerEventArgs args)
    {
        var player = TShock.Players[args.Who];
        if (player == null || !player.Active) return;

        if (_limboPlayers.ContainsKey(player.Index))
        {
            int pIndex = player.Index;
            string pName = player.Name;
            _mainThreadActions.Enqueue(() =>
            {
                var current = TShock.Players[pIndex];
                if (current != null && current.Active && current.Name == pName)
                {
                    current.GodMode = true; current.SetBuff(163, 360000, true); current.mute = true;
                    current.SendMessage(_config.Strings.LimboMessage, Color.White);
                }
            });
        }
    }

    private void VerifyCommand(CommandArgs args)
    {
        if (!_limboPlayers.ContainsKey(args.Player.Index)) { args.Player.SendInfoMessage("Already verified."); return; }

        string ip = args.Player.IP;
        var now = DateTime.UtcNow;

        var strikeData = _verifyStrikes.GetOrAdd(ip, (0, now));
        if ((now - strikeData.FirstStrike).TotalMinutes > 15)
        {
            strikeData = (0, now);
            _verifyStrikes[ip] = strikeData;
        }

        if (strikeData.Strikes >= 5)
        {
            SafeDisconnect(args.Player, "Disconnected: Too many invalid PIN attempts. Please wait 15 minutes before trying again.");
            return;
        }

        if (args.Parameters.Count == 0 || !_discord.PendingPins.TryGetValue(args.Parameters[0], out var data))
        {
            var newStrikes = strikeData.Strikes + 1;
            _verifyStrikes[ip] = (newStrikes, strikeData.FirstStrike);

            if (newStrikes >= 5)
            {
                SafeDisconnect(args.Player, "Disconnected: Too many invalid PIN attempts. Please wait 15 minutes before trying again.");
                return;
            }
            args.Player.SendErrorMessage($"Invalid PIN. Attempts remaining: {5 - newStrikes}");
            return;
        }

        if (DateTime.UtcNow > data.Expiry)
        {
            _discord.PendingPins.TryRemove(args.Parameters[0], out _);

            var newStrikes = strikeData.Strikes + 1;
            _verifyStrikes[ip] = (newStrikes, strikeData.FirstStrike);

            if (newStrikes >= 5)
            {
                SafeDisconnect(args.Player, "Disconnected: Too many invalid PIN attempts. Please wait 15 minutes before trying again.");
                return;
            }
            args.Player.SendErrorMessage($"Invalid PIN. Attempts remaining: {5 - newStrikes}");
            return;
        }

        if (_db.Ledger.TryGetValue(args.Player.Name.ToLower(), out var record))
        {
            if (record.DiscordId != data.DiscordId && !args.Player.IsLoggedIn)
            {
                args.Player.SendErrorMessage("Identity mismatch! Log in with your recovery password first to confirm ownership.");
                return;
            }
        }
        else if (TShock.UserAccounts.GetUserAccountByName(args.Player.Name) != null && !args.Player.IsLoggedIn)
        {
            args.Player.SendErrorMessage("This account already exists. Log in with your password first before linking to Discord.");
            return;
        }

        _verifyStrikes.TryRemove(ip, out _);
        _discord.PendingPins.TryRemove(args.Parameters[0], out _);

        int pIndex = args.Player.Index;
        string pName = args.Player.Name;
        string pUuid = args.Player.UUID;
        var pAccount = args.Player.Account;

        _ = Task.Run(() => FinalizeLinkage(pIndex, pName, pUuid, pAccount, data.DiscordId));
    }

    private void FinalizeLinkage(int pIndex, string pName, string pUuid, UserAccount? existingAccount, ulong discordId)
    {
        string? newPassword = null;
        string? newHashedPassword = null;
        string hashedUuid = BC.HashPassword(pUuid);

        // We evaluate account logic outside to avoid blocking main thread with BC.HashPassword
        var account = existingAccount ?? TShock.UserAccounts.GetUserAccountByName(pName);
        if (account == null)
        {
            // 🛡️ SECURITY: Use cryptographically secure RNG for temporary passwords to ensure maximum entropy
            var randomBytes = new byte[5];
            System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
            newPassword = Convert.ToHexString(randomBytes).ToLower();
            newHashedPassword = BC.HashPassword(newPassword);

            account = new UserAccount(pName, newHashedPassword, "", TShock.Config.Settings.DefaultRegistrationGroupName, DateTime.UtcNow.ToString("s"), DateTime.UtcNow.ToString("s"), "");
        }

        _mainThreadActions.Enqueue(() =>
        {
            var onlinePlayer = TShock.Players[pIndex];
            if (onlinePlayer != null && onlinePlayer.Active && onlinePlayer.Name == pName && onlinePlayer.UUID == pUuid)
            {
                onlinePlayer.GodMode = false;

                // FIX: Setting buff time to 0 safely clears it natively through TShock
                onlinePlayer.SetBuff(163, 0, true);

                if (onlinePlayer.Account == null)
                {
                    if (TShock.UserAccounts.GetUserAccountByName(onlinePlayer.Name) == null && account != null)
                    {
                        TShock.UserAccounts.AddUserAccount(account);
                    }
                    onlinePlayer.Account = account;
                }

                _limboPlayers.TryRemove(onlinePlayer.Index, out _);
                onlinePlayer.mute = false; onlinePlayer.Heal();
                onlinePlayer.SendMessage(_config.Strings.VerifySuccess, Color.LimeGreen);
            }
        });

        _ = _db.SaveSealAsync(new MetatronRecord(account!.Name, discordId, hashedUuid));

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            _mainThreadActions.Enqueue(() =>
            {
                var onlinePlayer = TShock.Players[pIndex];
                if (onlinePlayer != null && onlinePlayer.Active && onlinePlayer.Name == pName && onlinePlayer.UUID == pUuid)
                {
                    NetMessage.SendData(3, pIndex);
                    NetMessage.SendData(7, pIndex);
                }
            });
        });
        _ = _discord.PostLinkSuccessAsync(discordId, pName);
        if (newPassword != null && _config.ShowTemporaryPasswords) _ = _discord.SendRecoveryPasswordAsync(discordId, pName, newPassword);
    }

    private void UnlinkCommand(CommandArgs args)
    {
        if (args.Player.Account == null || !_db.Ledger.TryGetValue(args.Player.Account.Name.ToLower(), out var record)) { args.Player.SendErrorMessage("Not linked."); return; }
        _db.Ledger.TryRemove(record.AccountName.ToLower(), out _);
        _ = _db.RemoveSealAsync(record.DiscordId); 
        SafeDisconnect(args.Player, "Seal severed.");
    }

    private void OnPulse(EventArgs args)
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            action();
        }

        if (++_tickCounter < 60) return;
        _tickCounter = 0;
        if (_limboPlayers.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var kvp in _limboPlayers)
        {
            if (kvp.Value == DateTime.MinValue) continue; 
            if ((now - kvp.Value).TotalMinutes >= _config.VerificationTimeoutMinutes)
            {
                var p = TShock.Players[kvp.Key];
                if (p != null) SafeDisconnect(p, "Verification timeout.");
                _limboPlayers.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void OnLeave(LeaveEventArgs args) { _limboPlayers.TryRemove(args.Who, out _); }

    private void SafeDisconnect(TSPlayer player, string reason)
    {
        int index = player.Index;
        string name = player.Name;
        _mainThreadActions.Enqueue(() =>
        {
            var current = TShock.Players[index];
            if (current != null && current.Active && current.Name == name) current.Disconnect(reason);
        });
    }

    public void KickAccount(string accountName, string reason)
    {
        _mainThreadActions.Enqueue(() =>
        {
            TShock.Players.FirstOrDefault(p => p?.Account?.Name.ToLower() == accountName)?.Disconnect(reason);
        });
    }

    private void OnKickRequested(string accountName, string reason)
    {
        KickAccount(accountName, reason);
    }
}
