using System;
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

public partial class MetatronPlugin
{
    private DiscordRestClient? _discordRest;
    private IRestMessageChannel? _cachedLinkChannel;
    private ulong _lastProcessedMessageId = 0;
    private ulong _statusMessageId = 0;
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private bool _isPolling = false; 

    // NEW: Dedicated Engine Variables
    private System.Timers.Timer? _discordTimer; 
    private DateTime _lastDiscordPoll = DateTime.MinValue;
    private DateTime _lastDiscordHeartbeat = DateTime.MinValue;

    private async Task InitializeDiscordRestAsync()
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

            // NEW: Start the independent heartbeat/poll timer
            _discordTimer = new System.Timers.Timer(1000);
            _discordTimer.Elapsed += OnDiscordTimerPulse;
            _discordTimer.Start();
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Discord Login Failed: {ex.Message}"); }
    }

    private void OnDiscordTimerPulse(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_config.EnableDiscordGate) return;

        var now = DateTime.UtcNow;

        bool hasPlayers = TShock.Players.Any(p => p != null && p.Active);
        int currentPollInterval = hasPlayers ? _config.PollIntervalSeconds : 60; 

        if ((now - _lastDiscordPoll).TotalSeconds >= currentPollInterval)
        {
            _lastDiscordPoll = now;
            _ = Task.Run(() => PollLinkChannelAsync());
        }

        if ((now - _lastDiscordHeartbeat).TotalSeconds >= 150)
        {
            _lastDiscordHeartbeat = now;
            _ = Task.Run(() => UpdateStatusMessageAsync(true));
        }
    }

    private async Task UpdateStatusMessageAsync(bool isOnline)
    {
        if (_discordRest == null || _cachedLinkChannel == null) return;
        if (!_statusLock.Wait(0)) return; 

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
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Heartbeat Error: {ex.Message}"); }
        finally { _statusLock.Release(); }
    }
    
    private async Task<string> GetDiscordNameAsync(ulong userId)
    {
        if (_discordRest == null) return "Unknown User";
        try
        {
            var user = await _discordRest.GetUserAsync(userId);
            return user != null ? $"{user.Username} (@{user.GlobalName ?? user.Username})" : $"ID: {userId}";
        }
        catch { return $"ID: {userId} (Lookup Failed)"; }
    }

    private async Task<bool> CheckUserRoleRestAsync(ulong userId)
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
            
            return doc.RootElement.GetProperty("roles").EnumerateArray()
                      .Any(role => role.GetString() == _config.RequiredDiscordRoleId.ToString());
        }
        catch { return false; }
    }

    private async Task PostDiscordMessageRestAsync(ulong channelId, string content)
    {
        if (channelId == 0) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channelId}/messages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _config.DiscordBotToken);
            var payload = new { content = content };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await _httpClient.SendAsync(request);
        }
        catch { }
    }
}