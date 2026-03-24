using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Metatron.DiscordStub;
using TShockAPI;

#nullable enable

namespace Metatron;

public class DiscordService
{
    private readonly CoreConfig _config;
    private readonly DatabaseService _db;
    
    private DiscordRestClient? _discordRest;
    private RestMessageChannel? _cachedLinkChannel;
    private ulong _lastProcessedMessageId = 0;
    private ulong _statusMessageId = 0;
    
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private DateTime _lastDiscordHeartbeat = DateTime.MinValue;
    private readonly CancellationTokenSource _engineCts = new();

    public ConcurrentDictionary<string, (ulong DiscordId, DateTime Expiry)> PendingPins { get; } = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _scribeRateLimit = new();

    public DiscordService(CoreConfig config, DatabaseService db)
    {
        _config = config;
        _db = db;
    }

    public async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.DiscordBotToken)) return;

        try
        {
            _discordRest = new DiscordRestClient();
            await _discordRest.LoginAsync(TokenType.Bot, _config.DiscordBotToken);
            
            if (_config.LinkChannelId != 0)
            {
                _cachedLinkChannel = await _discordRest.GetChannelAsync(_config.LinkChannelId);
                if (_cachedLinkChannel != null)
                {
                    var initialMsgs = await _cachedLinkChannel.GetMessagesAsync(1);
                    var lastMsg = initialMsgs.FirstOrDefault();
                    if (lastMsg != null) _lastProcessedMessageId = lastMsg.Id;
                }
            }

            TShock.Log.ConsoleInfo("[Metatron] Facade Scribe Engine initialized with Rate Limiting.");
            _lastDiscordHeartbeat = DateTime.UtcNow; 
            
            _ = Task.Run(() => UpdateStatusMessageAsync(true));
            _ = Task.Run(() => PollingEngineAsync(), _engineCts.Token);
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Discord Init Failed: {ex.Message}"); }
    }

    public void Stop()
    {
        _engineCts.Cancel();
        try { UpdateStatusMessageAsync(false).GetAwaiter().GetResult(); } catch { }
    }

    private async Task PollingEngineAsync()
    {
        while (!_engineCts.IsCancellationRequested && !MetatronPlugin.IsShuttingDown)
        {
            try
            {
                var now = DateTime.UtcNow;

                var expiredPins = PendingPins.Where(kvp => now > kvp.Value.Expiry).Select(kvp => kvp.Key).ToList();
                foreach (var pin in expiredPins) PendingPins.TryRemove(pin, out _);

                await PollLinkChannelAsync();

                if ((now - _lastDiscordHeartbeat).TotalSeconds >= 150)
                {
                    _lastDiscordHeartbeat = now;
                    _ = UpdateStatusMessageAsync(true);
                }

                bool hasPlayers = TShock.Players.Any(p => p != null && p.Active);
                int sleepSeconds = hasPlayers ? _config.PollIntervalSeconds : 60; 

                await Task.Delay(TimeSpan.FromSeconds(sleepSeconds), _engineCts.Token);
            }
            catch (TaskCanceledException) { break; } 
            catch { }
        }
    }

    private async Task PollLinkChannelAsync()
    {
        if (_discordRest == null || _cachedLinkChannel == null) return;

        try
        {
            var messages = _lastProcessedMessageId == 0 
                ? await _cachedLinkChannel.GetMessagesAsync(10)
                : await _cachedLinkChannel.GetMessagesAsync(_lastProcessedMessageId, Direction.After, 50);

            var orderedMsgs = messages.OrderBy(m => m.Timestamp).ToList();
            if (!orderedMsgs.Any()) return;
            
            foreach (var msg in orderedMsgs)
            {
                _lastProcessedMessageId = msg.Id; 
                if (msg.Author.IsBot) continue;

                if (msg.Content.StartsWith("!link", StringComparison.OrdinalIgnoreCase)) await HandleLinkAsync(_cachedLinkChannel, msg);
                else if (msg.Content.StartsWith("!unlink", StringComparison.OrdinalIgnoreCase)) await HandleUnlinkAsync(_cachedLinkChannel, msg);
            }
        }
        catch { }
    }

    private async Task HandleLinkAsync(RestMessageChannel channel, RestMessage msg)
    {
        if (_scribeRateLimit.TryGetValue(msg.Author.Id, out var lastUse) && (DateTime.UtcNow - lastUse).TotalMinutes < 2)
        {
            await DeleteAndWarnAsync(channel, msg, $"⏳ <@{msg.Author.Id}>, stop requested. Wait a moment.");
            return;
        }

        if (_db.Ledger.Values.Any(r => r.DiscordId == msg.Author.Id))
        {
            await DeleteAndWarnAsync(channel, msg, $"ℹ️ <@{msg.Author.Id}>, your Discord account is already linked to a Celestial Seal.");
            return;
        }

        bool hasRole = await CheckUserRoleAsync(msg.Author.Id);
        if (_config.RequiredDiscordRoleId != 0 && !hasRole)
        {
            await DeleteAndWarnAsync(channel, msg, $"❌ <@{msg.Author.Id}>, you lack the required role to link.");
            return;
        }

        try 
        {
            string pin = Random.Shared.Next(100000, 999999).ToString();
            
            var dm = await msg.Author.CreateDMChannelAsync();
            if (dm != null)
            {
                await dm.SendMessageAsync($"📜 **Authorization PIN:** `{pin}`\nExpires in 15 mins. Enter this PIN as your Server Password in Terraria.");
                
                PendingPins[pin] = (msg.Author.Id, DateTime.UtcNow.AddMinutes(15));
                _scribeRateLimit[msg.Author.Id] = DateTime.UtcNow;

                TShock.Log.ConsoleInfo($"[Metatron] Discord User {msg.Author.Username} requested a linking PIN.");
                await DeleteAndWarnAsync(channel, msg, $"✅ <@{msg.Author.Id}>, check your DMs for your PIN!");
            }
            else throw new Exception();
        }
        catch 
        {
            await DeleteAndWarnAsync(channel, msg, $"❌ <@{msg.Author.Id}>, I couldn't DM you! Enable DMs and try again.");
        }
    }

    private async Task HandleUnlinkAsync(RestMessageChannel channel, RestMessage msg)
    {
        var record = _db.Ledger.Values.FirstOrDefault(r => r.DiscordId == msg.Author.Id);
        if (record != null && _db.Ledger.TryRemove(record.AccountName.ToLower(), out _))
        {
            _ = _db.RemoveSealAsync(msg.Author.Id);
            var player = TShock.Players.FirstOrDefault(p => p?.Account?.Name.ToLower() == record.AccountName.ToLower());
            player?.Disconnect("Your Discord authorization has been revoked via the Discord channel.");

            TShock.Log.ConsoleInfo($"[Metatron] Discord User {msg.Author.Username} severed their Celestial Seal.");
            await DeleteAndWarnAsync(channel, msg, $"✅ <@{msg.Author.Id}>, your Celestial Seal has been severed.");
            return;
        }
        await DeleteAndWarnAsync(channel, msg, $"❌ <@{msg.Author.Id}>, you do not have an active seal to sever.");
    }

    private async Task DeleteAndWarnAsync(RestMessageChannel channel, RestMessage triggerMsg, string warning)
    {
        var warnMsg = await channel.SendMessageAsync(warning);
        try { await triggerMsg.DeleteAsync(); } catch { }
        
        if (warnMsg != null)
        {
            _ = Task.Delay(5000).ContinueWith(async _ => { try { await warnMsg.DeleteAsync(); } catch { } });
        }
    }

    private async Task UpdateStatusMessageAsync(bool isOnline)
    {
        if (_discordRest == null || _cachedLinkChannel == null || !_statusLock.Wait(0)) return; 
        try
        {
            long unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string statusText = isOnline 
                ? $"🟢 **SERVER IS ONLINE**\nWelcome! Type `!link` in this channel to securely connect your Discord account to the server.\n\n⏱️ *Last Scribe Heartbeat:* <t:{unixTimestamp}:f> (<t:{unixTimestamp}:R>)"
                : $"🔴 **SERVER IS OFFLINE**\nThe verification gate is currently closed. Please check back later.\n\n🛑 *Server Shutdown:* <t:{unixTimestamp}:f>";

            if (_statusMessageId == 0)
            {
                var pins = await _cachedLinkChannel.GetPinnedMessagesAsync();
                var existingPin = pins.FirstOrDefault(p => p.Author.Id == _discordRest.CurrentUser.Id);
                
                if (existingPin != null)
                {
                    _statusMessageId = existingPin.Id;
                    await existingPin.ModifyAsync(statusText);
                }
                else
                {
                    var newMsg = await _cachedLinkChannel.SendMessageAsync(statusText);
                    if (newMsg != null)
                    {
                        _statusMessageId = newMsg.Id;
                        await newMsg.PinAsync();
                    }
                }
            }
            else
            {
                // We have the ID, but we don't have the object in memory. 
                // We'll use a fast raw patch rather than fetching the whole message first to save API calls.
                await _discordRest.PatchJsonAsync($"/channels/{_config.LinkChannelId}/messages/{_statusMessageId}", new { content = statusText });
            }
        }
        catch { }
        finally { _statusLock.Release(); }
    }

    private async Task<bool> CheckUserRoleAsync(ulong userId)
    {
        if (_config.DiscordGuildId == 0 || _config.RequiredDiscordRoleId == 0 || _discordRest == null) return true;
        try
        {
            var guild = await _discordRest.GetGuildAsync(_config.DiscordGuildId);
            if (guild == null) return false;

            var user = await guild.GetUserAsync(userId);
            if (user == null) return false;

            return user.RoleIds.Contains(_config.RequiredDiscordRoleId);
        }
        catch { return false; }
    }

    public async Task PostLinkSuccessAsync(ulong discordId, string characterName)
    {
        if (_cachedLinkChannel == null) return;
        try 
        {
            await _cachedLinkChannel.SendMessageAsync($"✨ **The Celestial Ledger updates...**\n<@{discordId}> has forged their seal as `{characterName}` and entered the realm!");
        } catch { }
    }
}
public async Task SendRecoveryPasswordAsync(ulong discordId, string characterName, string password)
    {
        if (_api == null) return;
        try
        {
            // 1. Open the DM Channel natively
            var dmChannelJson = await _api.PostAsync("/users/@me/channels", new { recipient_id = discordId.ToString() });
            if (dmChannelJson != null)
            {
                string dmChannelId = dmChannelJson.Value.GetProperty("id").GetString()!;
                
                // 2. Format and send the recovery message
                string msg = $"✨ **Celestial Seal Forged!** Your account `{characterName}` is securely linked.\n\n🔑 **Your TShock Recovery Password:** `{password}`\n\n*Keep this safe! Metatron uses frictionless UUID login, so you will only need this password if you connect from a new computer or lose your UUID.*";
                
                await _api.PostAsync($"/channels/{dmChannelId}/messages", new { content = msg });
            }
        }
        catch { TShock.Log.ConsoleError("[Metatron] Failed to DM recovery password."); }
    }
