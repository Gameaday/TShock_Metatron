using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Metatron;

public record MetatronRecord(string AccountName, ulong DiscordId, string Uuid);

public class CoreConfig
{
    public bool EnableDiscordGate { get; set; } = false;
    public bool EnableFrictionlessAuth { get; set; } = true;
    public bool EnableBroadcaster { get; set; } = true;

    public string DiscordBotToken { get; set; } = "";
    public ulong DiscordGuildId { get; set; } = 0;
    public ulong RequiredDiscordRoleId { get; set; } = 0;

    // Polling Setup
    public ulong LinkChannelId { get; set; } = 0;
    public int PollIntervalSeconds { get; set; } = 20;
    public int VerificationTimeoutMinutes { get; set; } = 3;

    public int GeneratedPasswordLength { get; set; } = 10;
    public bool RequireStrictIPForAutoLogin { get; set; } = false;
    public bool ShowTemporaryPasswords { get; set; } = true;

    public string GlobalDiscordWebhookUrl { get; set; } = "";

    public string StreamerName { get; set; } = "YourStreamerName";
    public string StreamUrl { get; set; } = "https://twitch.tv/YourChannel";
    public ulong StreamAnnouncementChannelId { get; set; } = 0;
}

public class Broadcast
{
    public string Name { get; set; } = "";
    public List<string> TriggerTypes { get; set; } = new();
    public bool Enabled { get; set; }
    
    // NEW: Dedicated list for TShock commands
    public List<string> Commands { get; set; } = new(); 
    
    public List<string> Messages { get; set; } = new();
    public List<string> TriggerWords { get; set; } = new();
    public List<string> TriggerNPCs { get; set; } = new();
    public List<string> TriggerRegions { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public string Permission { get; set; } = "";
    public List<string> AllowedDays { get; set; } = new();
    public List<string> Conditions { get; set; } = new();
    public string DiscordWebhookUrl { get; set; } = "";
    public string DiscordTitle { get; set; } = "";
    public string DiscordUsername { get; set; } = "";
    public string DiscordPingRole { get; set; } = "";
    public string ColorHex { get; set; } = "#FFFFFF";
    public bool HideTriggerText { get; set; } = false;
    public bool TriggerToWholeGroup { get; set; } = true;

    [JsonIgnore]
    public Color TextColor
    {
        get
        {
            try { return new Color(Convert.ToInt32(ColorHex.Substring(1, 2), 16), Convert.ToInt32(ColorHex.Substring(3, 2), 16), Convert.ToInt32(ColorHex.Substring(5, 2), 16)); }
            catch { return Color.White; }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CoreConfig))]
[JsonSerializable(typeof(List<Broadcast>))]
internal partial class MetatronJsonContext : JsonSerializerContext { }