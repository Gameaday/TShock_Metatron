using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using TShockAPI;

#nullable enable

namespace Metatron;

public class DiscordService
{
    private readonly CoreConfig _config;
    private readonly DatabaseService _db;
    
    private DiscordRestClient? _discordRest;
    private IRestMessageChannel? _cachedLinkChannel;
    private ulong _lastProcessedMessageId = 0;
    private ulong _statusMessageId = 0;
    
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private bool _isPolling = false;
    private System.Timers.Timer? _discordTimer; 
    private DateTime _lastDiscordPoll = DateTime.MinValue;
    private DateTime _lastDiscordHeartbeat = DateTime.MinValue;
    private static readonly HttpClient _httpClient = new();

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
                _cachedLinkChannel = await _discordRest.GetChannelAsync(_config.LinkChannelId) as IRestMessageChannel;
                if (_cachedLinkChannel != null)
                {
                    var initialMsgs = await _cachedLinkChannel.GetMessagesAsync(1).FlattenAsync();
                    var lastMsg = initialMsgs.FirstOrDefault();
                    if (lastMsg != null) _lastProcessedMessageId = lastMsg.Id;
                }
            }

            TShock.Log.ConsoleInfo("[Metatron] Scribe initialized on a dedicated background thread.");
            _lastDiscordHeartbeat = DateTime.UtcNow; 
            _lastDiscordPoll = DateTime.UtcNow; 

            _ = Task.Run(() => UpdateStatusMessageAsync(true));

            _discordTimer = new System.Timers.Timer(1000);
            _discordTimer.Elapsed += OnTimerPulse;
            _discordTimer.Start();
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Discord Login Failed: {ex.Message}"); }
    }

    public void Stop()
    {
        _discordTimer?.Stop();
        try { UpdateStatusMessageAsync(false).GetAwaiter().GetResult(); } catch { }
    }

    private void OnTimerPulse(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (MetatronPlugin.IsShuttingDown) return;
        var now = DateTime.UtcNow;
        
        // Clean expired PINs
        var expiredPins = PendingPins.Where(kvp => now > kvp.Value.Expiry).Select(kvp => kvp.Key).ToList();
        foreach (var expiredPin in expiredPins) PendingPins.TryRemove(expiredPin, out _);

        bool hasPlayers = TShock.Players.Any(p => p != null && p.Active);
        int currentPollInterval = hasPlayers ? _config.PollIntervalSeconds : 60; 

        if ((now - _lastDiscordPoll).TotalSeconds >= currentPollInterval)
        {
            _lastDiscordPoll = now;
            _ = Task.Run(PollLinkChannelAsync);
        }

        if ((now - _lastDiscordHeartbeat).TotalSeconds >= 150)
        {
            _lastDiscordHeartbeat = now;
            _ = Task.Run(() => UpdateStatusMessageAsync(true));
        }
    }

    private async Task PollLinkChannelAsync()
    {
        if (_discordRest == null || _cachedLinkChannel == null || _isPolling || MetatronPlugin.IsShuttingDown) return;
        _isPolling = true;

        try
        {
            var messages = _lastProcessedMessageId == 0 
                ? await _cachedLinkChannel.GetMessagesAsync(10).FlattenAsync() 
                : await _cachedLinkChannel.GetMessagesAsync(_lastProcessedMessageId, Direction.After, 50).FlattenAsync();

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
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Scribe Poll Error: {ex.Message}"); }
        finally { _isPolling = false; }
    }

    private async Task HandleLinkAsync(IRestMessageChannel channel, IMessage msg)
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
            await dm.SendMessageAsync($"📜 **Authorization PIN:** `{pin}`\nExpires in 15 mins. Enter this PIN as your Server Password in Terraria.");
            
            PendingPins[pin] = (msg.Author.Id, DateTime.UtcNow.AddMinutes(15));
            _scribeRateLimit[msg.Author.Id] = DateTime.UtcNow;

            TShock.Log.ConsoleInfo($"[Metatron] Discord User {msg.Author.Username} requested a linking PIN.");
            await DeleteAndWarnAsync(channel, msg, $"✅ <@{msg.Author.Id}>, check your DMs for your PIN!");
        }
        catch 
        {
            await DeleteAndWarnAsync(channel, msg, $"❌ <@{msg.Author.Id}>, I couldn't DM you! Enable DMs and try again.");
        }
    }

    private async Task HandleUnlinkAsync(IRestMessageChannel channel, IMessage msg)
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

    private async Task DeleteAndWarnAsync(IRestMessageChannel channel, IMessage msg, string warning)
    {
        var warnMsg = await channel.SendMessageAsync(warning);
        try { await msg.DeleteAsync(); } catch { }
        _ = Task.Delay(5000).ContinueWith(async _ => { try { await warnMsg.DeleteAsync(); } catch { } });
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
                var existing = pins.FirstOrDefault(p => p.Author.Id == _discordRest.CurrentUser.Id);
                
                if (existing != null)
                {
                    _statusMessageId = existing.Id;
                    await ((IUserMessage)existing).ModifyAsync(m => m.Content = statusText);
                }
                else
                {
                    var newMsg = await _cachedLinkChannel.SendMessageAsync(statusText);
                    await newMsg.PinAsync();
                    _statusMessageId = newMsg.Id;
                }
            }
            else
            {
                var msg = await _cachedLinkChannel.GetMessageAsync(_statusMessageId) as IUserMessage;
                if (msg != null) await msg.ModifyAsync(m => m.Content = statusText);
                else _statusMessageId = 0; 
            }
        }
        catch { }
        finally { _statusLock.Release(); }
    }

    private async Task<bool> CheckUserRoleAsync(ulong userId)
    {
        if (_config.DiscordGuildId == 0 || _config.RequiredDiscordRoleId == 0) return true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{_config.DiscordGuildId}/members/{userId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _config.DiscordBotToken);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.GetProperty("roles").EnumerateArray().Any(role => role.GetString() == _config.RequiredDiscordRoleId.ToString());
        }
        catch { return false; }
    }

    public async Task<string> GetDiscordNameAsync(ulong userId)
    {
        if (_discordRest == null) return "Unknown User";
        try
        {
            var user = await _discordRest.GetUserAsync(userId);
            return user != null ? $"{user.Username} (@{user.GlobalName ?? user.Username})" : $"ID: {userId}";
        }
        catch { return $"ID: {userId} (Lookup Failed)"; }
    }

    // NEW: The Celebration Broadcaster
    public async Task PostLinkSuccessAsync(ulong discordId, string characterName)
    {
        if (_cachedLinkChannel == null) return;
        try 
        {
            await _cachedLinkChannel.SendMessageAsync($"✨ **The Celestial Ledger updates...**\n<@{discordId}> has forged their seal as `{characterName}` and entered the realm!");
        } 
        catch { TShock.Log.ConsoleError("[Metatron] Failed to post success message to Discord."); }
    }
}
