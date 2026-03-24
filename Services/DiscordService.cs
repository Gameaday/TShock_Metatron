using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TShockAPI;

#nullable enable

namespace Metatron;

public class DiscordService
{
    private readonly CoreConfig _config;
    private readonly DatabaseService _db;
    
    // NEW: Our custom micro-library engine!
    private DiscordWebClient? _api;
    
    private ulong _lastProcessedMessageId = 0;
    private ulong _statusMessageId = 0;
    private ulong _botUserId = 0;
    
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
            _api = new DiscordWebClient(_config.DiscordBotToken);

            var meJson = await _api.GetAsync("/users/@me");
            if (meJson != null) _botUserId = ulong.Parse(meJson.Value.GetProperty("id").GetString()!);

            TShock.Log.ConsoleInfo("[Metatron] Native Scribe Engine initialized with Rate Limiting.");
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
        if (_config.LinkChannelId == 0 || _api == null) return;

        try
        {
            string url = _lastProcessedMessageId == 0 
                ? $"/channels/{_config.LinkChannelId}/messages?limit=10"
                : $"/channels/{_config.LinkChannelId}/messages?after={_lastProcessedMessageId}&limit=50";

            var messages = await _api.GetAsync(url);
            if (messages == null || messages.Value.ValueKind != JsonValueKind.Array) return;

            var msgsArray = messages.Value.EnumerateArray().Reverse().ToList(); 
            
            foreach (var msg in msgsArray)
            {
                _lastProcessedMessageId = ulong.Parse(msg.GetProperty("id").GetString()!);
                
                var author = msg.GetProperty("author");
                if (author.TryGetProperty("bot", out var isBot) && isBot.GetBoolean()) continue;

                string content = msg.GetProperty("content").GetString() ?? "";
                ulong authorId = ulong.Parse(author.GetProperty("id").GetString()!);
                string authorName = author.GetProperty("username").GetString() ?? "Unknown";

                if (content.StartsWith("!link", StringComparison.OrdinalIgnoreCase)) await HandleLinkAsync(authorId, authorName, _lastProcessedMessageId);
                else if (content.StartsWith("!unlink", StringComparison.OrdinalIgnoreCase)) await HandleUnlinkAsync(authorId, authorName, _lastProcessedMessageId);
            }
        }
        catch { }
    }

    private async Task HandleLinkAsync(ulong authorId, string authorName, ulong msgId)
    {
        if (_api == null) return;

        if (_scribeRateLimit.TryGetValue(authorId, out var lastUse) && (DateTime.UtcNow - lastUse).TotalMinutes < 2)
        {
            await DeleteAndWarnAsync(msgId, $"⏳ <@{authorId}>, stop requested. Wait a moment.");
            return;
        }

        if (_db.Ledger.Values.Any(r => r.DiscordId == authorId))
        {
            await DeleteAndWarnAsync(msgId, $"ℹ️ <@{authorId}>, your Discord account is already linked to a Celestial Seal.");
            return;
        }

        bool hasRole = await CheckUserRoleAsync(authorId);
        if (_config.RequiredDiscordRoleId != 0 && !hasRole)
        {
            await DeleteAndWarnAsync(msgId, $"❌ <@{authorId}>, you lack the required role to link.");
            return;
        }

        try 
        {
            string pin = Random.Shared.Next(100000, 999999).ToString();
            
            var dmChannelJson = await _api.PostAsync("/users/@me/channels", new { recipient_id = authorId.ToString() });
            if (dmChannelJson == null) throw new Exception();
            
            string dmChannelId = dmChannelJson.Value.GetProperty("id").GetString()!;

            await _api.PostAsync($"/channels/{dmChannelId}/messages", new { content = $"📜 **Authorization PIN:** `{pin}`\nExpires in 15 mins. Enter this PIN as your Server Password in Terraria." });
            
            PendingPins[pin] = (authorId, DateTime.UtcNow.AddMinutes(15));
            _scribeRateLimit[authorId] = DateTime.UtcNow;

            TShock.Log.ConsoleInfo($"[Metatron] Discord User {authorName} requested a linking PIN.");
            await DeleteAndWarnAsync(msgId, $"✅ <@{authorId}>, check your DMs for your PIN!");
        }
        catch 
        {
            await DeleteAndWarnAsync(msgId, $"❌ <@{authorId}>, I couldn't DM you! Enable DMs and try again.");
        }
    }

    private async Task HandleUnlinkAsync(ulong authorId, string authorName, ulong msgId)
    {
        var record = _db.Ledger.Values.FirstOrDefault(r => r.DiscordId == authorId);
        if (record != null && _db.Ledger.TryRemove(record.AccountName.ToLower(), out _))
        {
            _ = _db.RemoveSealAsync(authorId);
            var player = TShock.Players.FirstOrDefault(p => p?.Account?.Name.ToLower() == record.AccountName.ToLower());
            player?.Disconnect("Your Discord authorization has been revoked via the Discord channel.");

            TShock.Log.ConsoleInfo($"[Metatron] Discord User {authorName} severed their Celestial Seal.");
            await DeleteAndWarnAsync(msgId, $"✅ <@{authorId}>, your Celestial Seal has been severed.");
            return;
        }
        await DeleteAndWarnAsync(msgId, $"❌ <@{authorId}>, you do not have an active seal to sever.");
    }

    private async Task DeleteAndWarnAsync(ulong triggerMsgId, string warning)
    {
        if (_api == null) return;

        var warnMsg = await _api.PostAsync($"/channels/{_config.LinkChannelId}/messages", new { content = warning });
        _ = _api.DeleteAsync($"/channels/{_config.LinkChannelId}/messages/{triggerMsgId}"); 
        
        if (warnMsg != null)
        {
            string warnMsgId = warnMsg.Value.GetProperty("id").GetString()!;
            _ = Task.Delay(5000).ContinueWith(_ => _api.DeleteAsync($"/channels/{_config.LinkChannelId}/messages/{warnMsgId}"));
        }
    }

    private async Task UpdateStatusMessageAsync(bool isOnline)
    {
        if (_config.LinkChannelId == 0 || _api == null || !_statusLock.Wait(0)) return; 
        try
        {
            long unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string statusText = isOnline 
                ? $"🟢 **SERVER IS ONLINE**\nWelcome! Type `!link` in this channel to securely connect your Discord account to the server.\n\n⏱️ *Last Scribe Heartbeat:* <t:{unixTimestamp}:f> (<t:{unixTimestamp}:R>)"
                : $"🔴 **SERVER IS OFFLINE**\nThe verification gate is currently closed. Please check back later.\n\n🛑 *Server Shutdown:* <t:{unixTimestamp}:f>";

            if (_statusMessageId == 0)
            {
                var pins = await _api.GetAsync($"/channels/{_config.LinkChannelId}/pins");
                JsonElement? existingPin = null;
                
                if (pins != null && pins.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pin in pins.Value.EnumerateArray())
                    {
                        if (pin.GetProperty("author").GetProperty("id").GetString() == _botUserId.ToString())
                        {
                            existingPin = pin;
                            break;
                        }
                    }
                }
                
                if (existingPin != null)
                {
                    _statusMessageId = ulong.Parse(existingPin.Value.GetProperty("id").GetString()!);
                    await _api.PatchAsync($"/channels/{_config.LinkChannelId}/messages/{_statusMessageId}", new { content = statusText });
                }
                else
                {
                    var newMsg = await _api.PostAsync($"/channels/{_config.LinkChannelId}/messages", new { content = statusText });
                    if (newMsg != null)
                    {
                        _statusMessageId = ulong.Parse(newMsg.Value.GetProperty("id").GetString()!);
                        await _api.PatchAsync($"/channels/{_config.LinkChannelId}/pins/{_statusMessageId}", new { }); // Put pin
                    }
                }
            }
            else
            {
                var response = await _api.PatchAsync($"/channels/{_config.LinkChannelId}/messages/{_statusMessageId}", new { content = statusText });
                if (response == null) _statusMessageId = 0; 
            }
        }
        catch { }
        finally { _statusLock.Release(); }
    }

    private async Task<bool> CheckUserRoleAsync(ulong userId)
    {
        if (_config.DiscordGuildId == 0 || _config.RequiredDiscordRoleId == 0 || _api == null) return true;
        try
        {
            var member = await _api.GetAsync($"/guilds/{_config.DiscordGuildId}/members/{userId}");
            if (member == null) return false;

            return member.Value.GetProperty("roles").EnumerateArray().Any(role => role.GetString() == _config.RequiredDiscordRoleId.ToString());
        }
        catch { return false; }
    }

    public async Task PostLinkSuccessAsync(ulong discordId, string characterName)
    {
        if (_config.LinkChannelId == 0 || _api == null) return;
        try 
        {
            await _api.PostAsync($"/channels/{_config.LinkChannelId}/messages", new { content = $"✨ **The Celestial Ledger updates...**\n<@{discordId}> has forged their seal as `{characterName}` and entered the realm!" });
        } catch { }
    }
}
