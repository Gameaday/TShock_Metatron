using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Microsoft.Data.Sqlite;
using TShockAPI;

#nullable enable

namespace Metatron;

public partial class MetatronPlugin
{
    private async Task PollLinkChannelAsync()
    {
        if (_discordRest == null || _cachedLinkChannel == null || _isPolling || IsShuttingDown) return;
        _isPolling = true;

        try
        {
            // MAINTENANCE: Clean up expired PINs from RAM
            var expiredPins = _pendingPins.Where(kvp => DateTime.UtcNow > kvp.Value.Expiry).Select(kvp => kvp.Key).ToList();
            foreach (var expiredPin in expiredPins) _pendingPins.TryRemove(expiredPin, out _);

            // Fetch everything AFTER the last processed message to prevent dropping commands
            IEnumerable<IMessage> messages;
            if (_lastProcessedMessageId == 0)
            {
                messages = await _cachedLinkChannel.GetMessagesAsync(10).FlattenAsync();
            }
            else
            {
                messages = await _cachedLinkChannel.GetMessagesAsync(_lastProcessedMessageId, Direction.After, 50).FlattenAsync();
            }

            var orderedMsgs = messages.OrderBy(m => m.Timestamp).ToList();
            if (!orderedMsgs.Any()) return;
            
            foreach (var msg in orderedMsgs)
            {
                _lastProcessedMessageId = msg.Id; 

                if (msg.Author.IsBot) continue;

                if (msg.Content.StartsWith("!link", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleScribeRestLink(_cachedLinkChannel, msg);
                }
                else if (msg.Content.StartsWith("!unlink", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleScribeRestUnlink(_cachedLinkChannel, msg);
                }
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Scribe Poll Error: {ex.Message}"); }
        finally 
        { 
            _isPolling = false; 
        }
    }

    private async Task HandleScribeRestLink(IRestMessageChannel channel, IMessage msg)
    {
        if (_scribeRateLimit.TryGetValue(msg.Author.Id, out var lastUse) && (DateTime.UtcNow - lastUse).TotalMinutes < 2)
        {
            var warnMsg = await channel.SendMessageAsync($"⏳ <@{msg.Author.Id}>, stop requested. Wait a moment.");
            try { await msg.DeleteAsync(); } catch { }
            _ = Task.Delay(5000).ContinueWith(async _ => { try { await warnMsg.DeleteAsync(); } catch { } });
            return;
        }

        if (_ledger.Values.Any(r => r.DiscordId == msg.Author.Id))
        {
            var linkedMsg = await channel.SendMessageAsync($"ℹ️ <@{msg.Author.Id}>, your Discord account is already linked to a Celestial Seal.");
            try { await msg.DeleteAsync(); } catch { }
            _ = Task.Delay(5000).ContinueWith(async _ => { try { await linkedMsg.DeleteAsync(); } catch { } });
            return;
        }

        bool hasRole = await CheckUserRoleRestAsync(msg.Author.Id);
        if (_config.RequiredDiscordRoleId != 0 && !hasRole)
        {
            var roleMsg = await channel.SendMessageAsync($"❌ <@{msg.Author.Id}>, you lack the required role to link.");
            try { await msg.DeleteAsync(); } catch { }
            _ = Task.Delay(5000).ContinueWith(async _ => { try { await roleMsg.DeleteAsync(); } catch { } });
            return;
        }

        try 
        {
            string pin = Random.Shared.Next(100000, 999999).ToString();
            
            var dm = await msg.Author.CreateDMChannelAsync();
            await dm.SendMessageAsync($"📜 **Authorization PIN:** `{pin}`\nExpires in 15 mins. Enter this PIN as your Server Password in Terraria.");
            
            _pendingPins[pin] = (msg.Author.Id, DateTime.UtcNow.AddMinutes(15));
            _scribeRateLimit[msg.Author.Id] = DateTime.UtcNow;

            TShock.Log.ConsoleInfo($"[Metatron] Discord User {msg.Author.Username} requested a linking PIN.");

            var botReply = await channel.SendMessageAsync($"✅ <@{msg.Author.Id}>, check your DMs for your PIN!");

            try { await msg.DeleteAsync(); } catch { }
            _ = Task.Delay(5000).ContinueWith(async _ => { try { await botReply.DeleteAsync(); } catch { } });
        }
        catch 
        {
            var failMsg = await channel.SendMessageAsync($"❌ <@{msg.Author.Id}>, I couldn't DM you! Enable DMs and try again.");
            try { await msg.DeleteAsync(); } catch { }
            _ = Task.Delay(5000).ContinueWith(async _ => { try { await failMsg.DeleteAsync(); } catch { } });
        }
    }

    private async Task HandleScribeRestUnlink(IRestMessageChannel channel, IMessage msg)
    {
        var record = _ledger.Values.FirstOrDefault(r => r.DiscordId == msg.Author.Id);
        
        if (record != null)
        {
            if (_ledger.TryRemove(record.AccountName.ToLower(), out _))
            {
                _ = Task.Run(async () => {
                    await _dbLock.WaitAsync();
                    try {
                        using var conn = new SqliteConnection($"Data Source={DbPath}");
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "DELETE FROM Ledger WHERE DiscordId = @did";
                        cmd.Parameters.AddWithValue("@did", msg.Author.Id.ToString());
                        cmd.ExecuteNonQuery();
                    } catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] DB Delete Error: {ex.Message}"); }
                    finally { _dbLock.Release(); }
                });
                
                var player = TShock.Players.FirstOrDefault(p => p?.Account?.Name.ToLower() == record.AccountName.ToLower());
                player?.Disconnect("Your Discord authorization has been revoked via the Discord channel.");

                TShock.Log.ConsoleInfo($"[Metatron] Discord User {msg.Author.Username} severed their Celestial Seal.");

                var successMsg = await channel.SendMessageAsync($"✅ <@{msg.Author.Id}>, your Celestial Seal has been severed.");
                try { await msg.DeleteAsync(); } catch { }
                _ = Task.Delay(5000).ContinueWith(async _ => { try { await successMsg.DeleteAsync(); } catch { } });
                return;
            }
        }

        var failMsg = await channel.SendMessageAsync($"❌ <@{msg.Author.Id}>, you do not have an active seal to sever.");
        try { await msg.DeleteAsync(); } catch { }
        _ = Task.Delay(5000).ContinueWith(async _ => { try { await failMsg.DeleteAsync(); } catch { } });
    }
}