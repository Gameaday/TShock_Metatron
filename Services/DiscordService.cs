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

    // NEW: Chunked Audit Tracking
    private readonly ConcurrentQueue<string> _auditQueue = new();
    private DateTime _lastAuditRefill = DateTime.MinValue;
    private DateTime _lastActiveAudit = DateTime.MinValue;
    public ConcurrentDictionary<string, (ulong DiscordId, DateTime Expiry)> PendingPins { get; } = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _scribeRateLimit = new();

    public event Action<string, string>? KickRequested;

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
                var msgs = await _cachedLinkChannel?.GetMessagesAsync(1)!;
                if (msgs != null && msgs.Any()) _lastProcessedMessageId = msgs.First().Id;
            }

            TShock.Log.ConsoleInfo("[Metatron] Unified Scribe Engine initialized.");
            
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

    // THE SINGLE THREAD ENGINE
    private async Task PollingEngineAsync()
    {
        while (!_engineCts.IsCancellationRequested && !MetatronPlugin.IsShuttingDown)
        {
            try
            {
                var now = DateTime.UtcNow;
                bool hasPlayers = TShock.Players.Any(p => p != null && p.Active);

                // 1. PIN Cleanup
                var expired = PendingPins.Where(kvp => now > kvp.Value.Expiry).Select(kvp => kvp.Key).ToList();
                foreach (var pin in expired) PendingPins.TryRemove(pin, out _);

                // 2. Poll Discord Channel
                await PollLinkChannelAsync();

                // 3. Status Heartbeat
                if ((now - _lastDiscordHeartbeat).TotalSeconds >= 150)
                {
                    _lastDiscordHeartbeat = now;
                    _ = UpdateStatusMessageAsync(true);
                }
                
                // NEW: Active Player Fast-Track (Audits online players every 15 minutes)
                if (hasPlayers && (now - _lastActiveAudit).TotalMinutes >= 15)
                {
                    _lastActiveAudit = now;
                    var activeNames = TShock.Players.Select(p => p?.Account?.Name?.ToLower()).Where(name => name != null);
                    foreach (var name in activeNames) _auditQueue.Enqueue(name!);
                }
                
                // 4. CHUNKED AUDIT (Zero extra threads, zero blocking)
                if (_auditQueue.IsEmpty && (now - _lastAuditRefill).TotalHours >= _config.LedgerAuditIntervalHours)
                {
                    _lastAuditRefill = now;
                    foreach (var key in _db.Ledger.Keys) _auditQueue.Enqueue(key);
                }

                if (_auditQueue.TryDequeue(out string? accountName) && _db.Ledger.TryGetValue(accountName, out var record))
                {
                    bool? isStillValid = await CheckUserRoleAsync(record.DiscordId);
                    if (isStillValid == false)
                    {
                        TShock.Log.ConsoleInfo($"[Metatron] Audit: Severing seal for {record.AccountName} (No longer valid in Discord).");
                        if (_db.Ledger.TryRemove(accountName, out _))
                        {
                            await _db.RemoveSealAsync(record.DiscordId);
                            KickRequested?.Invoke(accountName, "✨ Celestial Seal severed: You are no longer in the Discord server or lack the required role.");
                        }
                    }
                }

                // 5. Dynamic Sleep
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
        if (_discordRest == null) return;

        var now = DateTime.UtcNow;
        while (true)
        {
            if (_scribeRateLimit.TryGetValue(msg.Author.Id, out var nextAllowed))
            {
                if (now < nextAllowed)
                {
                    await DeleteAndWarnAsync(channel, msg, $"⏳ <@{msg.Author.Id}>, stop requested. Wait a moment.");
                    return;
                }

                if (_scribeRateLimit.TryUpdate(msg.Author.Id, now.AddMinutes(2), nextAllowed))
                    break;
            }
            else if (_scribeRateLimit.TryAdd(msg.Author.Id, now.AddMinutes(2)))
            {
                break;
            }
        }

        bool? hasRole = await CheckUserRoleAsync(msg.Author.Id);
        if (_config.RequiredDiscordRoleId != 0 && hasRole != true)
        {
            await DeleteAndWarnAsync(channel, msg, $"❌ <@{msg.Author.Id}>, you lack the required role to link (or the API is down).");
            return;
        }

        try 
        {
            // 🛡️ SECURITY: Use cryptographically secure RNG for authorization PINs to prevent predictability
            string pin = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            var dm = await msg.Author.CreateDMChannelAsync();
            if (dm != null)
            {
                await dm.SendMessageAsync($"📜 **Authorization PIN:** `{pin}`\nExpires in 15 mins. Enter this PIN as your Server Password in Terraria.");
                PendingPins[pin] = (msg.Author.Id, DateTime.UtcNow.AddMinutes(15));

                TShock.Log.ConsoleInfo($"[Metatron] Discord User {msg.Author.Username} requested a linking PIN.");
                await DeleteAndWarnAsync(channel, msg, $"✅ <@{msg.Author.Id}>, check your DMs for your PIN!");
            }
        }
        catch { await DeleteAndWarnAsync(channel, msg, $"❌ <@{msg.Author.Id}>, enable DMs and try again."); }
    }

    private async Task HandleUnlinkAsync(RestMessageChannel channel, RestMessage msg)
    {
        var record = _db.Ledger.Values.FirstOrDefault(r => r.DiscordId == msg.Author.Id);
        if (record != null && _db.Ledger.TryRemove(record.AccountName.ToLower(), out _))
        {
            _ = _db.RemoveSealAsync(msg.Author.Id);
            KickRequested?.Invoke(record.AccountName.ToLower(), "Your Discord authorization has been revoked via Discord.");
            await DeleteAndWarnAsync(channel, msg, $"✅ <@{msg.Author.Id}>, your Celestial Seal has been severed.");
        }
        else await DeleteAndWarnAsync(channel, msg, $"❌ <@{msg.Author.Id}>, you do not have an active seal.");
    }

    private async Task DeleteAndWarnAsync(RestMessageChannel channel, RestMessage triggerMsg, string warning)
    {
        var warnMsg = await channel.SendMessageAsync(warning);
        try { await triggerMsg.DeleteAsync(); } catch { }
        if (warnMsg != null) _ = Task.Delay(5000).ContinueWith(async _ => { try { await warnMsg.DeleteAsync(); } catch { } });
    }

    private async Task UpdateStatusMessageAsync(bool isOnline)
    {
        if (_discordRest == null || _cachedLinkChannel == null || !_statusLock.Wait(0)) return; 
        try
        {
            long unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string statusText = isOnline 
                ? $"🟢 **SERVER IS ONLINE**\nType `!link` here to securely connect your Discord account.\n\n⏱️ *Heartbeat:* <t:{unixTimestamp}:R>"
                : $"🔴 **SERVER IS OFFLINE**\nThe gate is currently closed.\n\n🛑 *Shutdown:* <t:{unixTimestamp}:f>";

            if (_statusMessageId == 0)
            {
                var pins = await _cachedLinkChannel.GetPinnedMessagesAsync();
                var existing = pins.FirstOrDefault(p => p.Author.Id == _discordRest.CurrentUser.Id);
                if (existing != null) { _statusMessageId = existing.Id; await existing.ModifyAsync(statusText); }
                else { var newMsg = await _cachedLinkChannel.SendMessageAsync(statusText); if (newMsg != null) { _statusMessageId = newMsg.Id; await newMsg.PinAsync(); } }
            }
            else await _discordRest.PatchJsonAsync($"/channels/{_config.LinkChannelId}/messages/{_statusMessageId}", new { content = statusText });
        }
        catch { } finally { _statusLock.Release(); }
    }

    public async Task<bool?> CheckUserRoleAsync(ulong userId)
    {
        if (_discordRest == null || _config.DiscordGuildId == 0) return true;
        try
        {
            var guild = await _discordRest.GetGuildAsync(_config.DiscordGuildId);
            if (guild == null) return null; // API issue, fail open

            var user = await guild.GetUserAsync(userId);
            if (user == null) return false; // Left the server
            
            if (_config.RequiredDiscordRoleId != 0) return user.RoleIds.Contains(_config.RequiredDiscordRoleId); // Has role
            return true; // No role required, but is in server
        }
        catch { return null; } // Catch all network exceptions, fail open
    }

    public async Task PostLinkSuccessAsync(ulong discordId, string characterName)
    {
        if (_cachedLinkChannel == null) return;
        try { await _cachedLinkChannel.SendMessageAsync(string.Format(_config.Strings.DiscordBroadcast, discordId, characterName)); } catch { }
    }

    public async Task SendRecoveryPasswordAsync(ulong discordId, string characterName, string password)
    {
        if (_discordRest == null) return;
        try
        {
            var user = await _discordRest.GetUserAsync(discordId);
            if (user != null)
            {
                var dm = await user.CreateDMChannelAsync();
                if (dm != null) 
                {
                    await dm.SendMessageAsync($"✨ **Seal Forged!** Account: `{characterName}`\n🔑 **Recovery Password:** `{password}`\n*Keep this safe!*");
                }
            }
        }
        catch { }
    }
}
