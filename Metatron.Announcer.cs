using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

#nullable enable

namespace Metatron;

public partial class MetatronPlugin
{
    private int _tickCounter = 0;

    private void OnAnnouncerPulse(EventArgs args)
    {
        if (++_tickCounter < 60) return;
        _tickCounter = 0;

        var now = DateTime.UtcNow;

        // --- THE QUARANTINE SWEEPER ---
        if (_limboPlayers.Count > 0)
        {
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

        // --- BROADCASTER (Time Transitions) ---
        if (!_config.EnableBroadcaster || !TShock.Players.Any(p => p != null && p.Active)) return;

        if (Main.dayTime != _wasDayTime)
        {
            _wasDayTime = Main.dayTime;
            ProcessBroadcasts(Main.dayTime ? "Dawn" : "Dusk", "TimeTransition", null);
        }
    }

    private void ProcessBroadcasts(string trigger, string type, TSPlayer? target)
    {
        foreach (var bc in _allBroadcasts.Where(b => b.Enabled && b.TriggerTypes.Contains(type)))
        {
            if ((bc.TriggerWords.Count == 0 || bc.TriggerWords.Contains(trigger, StringComparer.OrdinalIgnoreCase) || type == "Join") 
                && CheckConditions(bc) && CheckAccess(bc, target) && CheckRegion(bc, target))
            {
                ExecuteBroadcast(bc, target);
            }
        }
    }

    private void OnAnnouncerGetData(GetDataEventArgs args)
    {
        if (args.Handled || !_config.EnableBroadcaster) return;
        
        if (args.MsgID == PacketTypes.PlayerDeathV2)
        {
            using var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length));
            int playerId = reader.ReadByte();
            var reason = Terraria.DataStructures.PlayerDeathReason.FromReader(reader);
            var player = TShock.Players[playerId];
            if (player == null || !player.Active) return;

            string deathMessage = reason.GetDeathText(player.Name).ToString();
            foreach (var bc in _allBroadcasts.Where(b => b.Enabled && b.TriggerTypes.Contains("Death")))
            {
                if (bc.TriggerWords.Count == 0 || bc.TriggerWords.Any(tw => deathMessage.Contains(tw, StringComparison.OrdinalIgnoreCase)))
                {
                    if (CheckRegion(bc, player) && CheckAccess(bc, player) && CheckConditions(bc))
                    {
                        ExecuteBroadcast(bc, player, deathMessage);
                    }
                }
            }
        }
    }

    private void OnAnnouncerChat(ServerChatEventArgs args)
    {
        if (args.Handled) return;
        string text = args.Text.ToLower();
        var player = TShock.Players[args.Who];
        if (player == null) return;

        foreach (var bc in _allBroadcasts.Where(b => b.Enabled && b.TriggerTypes.Contains("Chat")))
        {
            if (bc.TriggerWords.Any(tw => text.Contains(tw.ToLower())))
            {
                if (CheckRegion(bc, player) && CheckAccess(bc, player) && CheckConditions(bc))
                {
                    ExecuteBroadcast(bc, player);
                    if (bc.HideTriggerText) args.Handled = true;
                }
            }
        }
    }

    private void OnAnnouncerNpcKilled(NpcKilledEventArgs args)
    {
        var npc = args.npc;
        foreach (var bc in _allBroadcasts.Where(b => b.Enabled && b.TriggerTypes.Contains("NPCKill")))
        {
            if (bc.TriggerNPCs.Count > 0 && !bc.TriggerNPCs.Any(n => n.Equals(npc.FullName, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (CheckConditions(bc)) ExecuteBroadcast(bc, null);
        }
    }

    private bool CheckAccess(Broadcast bc, TSPlayer? player)
    {
        if (player == null) return true;
        if (!string.IsNullOrWhiteSpace(bc.Permission) && !player.HasPermission(bc.Permission)) return false;
        return bc.Groups.Count == 0 || bc.Groups.Contains("*") || bc.Groups.Any(g => player.Group.Name == g);
    }

    private bool CheckRegion(Broadcast bc, TSPlayer? player)
    {
        if (player == null || bc.TriggerRegions.Count == 0) return true;
        var reg = TShock.Regions.GetTopRegion(TShock.Regions.InAreaRegion(player.TileX, player.TileY));
        return reg != null && bc.TriggerRegions.Contains(reg.Name);
    }

    private bool CheckConditions(Broadcast bc)
    {
        if (bc.AllowedDays.Count > 0 && !bc.AllowedDays.Contains(DateTime.Now.DayOfWeek.ToString(), StringComparer.OrdinalIgnoreCase)) return false;
        if (bc.Conditions.Count == 0) return true;

        foreach (var cond in bc.Conditions.Select(c => c.ToLower()))
        {
            if (cond == "raining" && !Main.raining) return false;
            if (cond == "day" && !Main.dayTime) return false;
            if (cond == "night" && Main.dayTime) return false;
            if (cond == "bloodmoon" && !Main.bloodMoon) return false;
            if (cond == "streaming" && !IsStreaming) return false;
        }
        return true;
    }

    private void ExecuteBroadcast(Broadcast bc, TSPlayer? target, string? specialContext = null)
    {
        // 1. Run all native TShock commands (like /time dawn)
        foreach (var cmd in bc.Commands)
        {
            string formattedCmd = cmd.Replace("{player}", target?.Name ?? "Server")
                                     .Replace("{world}", Main.worldName)
                                     .Replace("{context}", specialContext ?? "");
            
            Commands.HandleCommand(TSPlayer.Server, formattedCmd.TrimStart('/'));
        }

        // 2. Broadcast a random message to chat and Discord
        if (bc.Messages.Count > 0)
        {
            string msg = bc.Messages[Random.Shared.Next(bc.Messages.Count)]
                .Replace("{player}", target?.Name ?? "Server")
                .Replace("{world}", Main.worldName)
                .Replace("{context}", specialContext ?? "");

            if (bc.TriggerToWholeGroup) TSPlayer.All.SendMessage(msg, bc.TextColor);
            else target?.SendMessage(msg, bc.TextColor);

            string targetWebhook = !string.IsNullOrWhiteSpace(bc.DiscordWebhookUrl) ? bc.DiscordWebhookUrl : _config.GlobalDiscordWebhookUrl;
            if (!string.IsNullOrWhiteSpace(targetWebhook))
            {
                var payload = new { embeds = new[] { new { description = msg, color = (bc.TextColor.R << 16) | (bc.TextColor.G << 8) | bc.TextColor.B } } };
                _ = _httpClient.PostAsync(targetWebhook, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            }
        }
    }
}