using System.Text.Json.Serialization;

namespace Metatron;

public class CoreConfig
{
    public bool EnableDiscordGate { get; set; } = true;
    public bool EnableFrictionlessAuth { get; set; } = true;

    public string DiscordBotToken { get; set; } = "";
    public ulong DiscordGuildId { get; set; } = 0;
    public ulong RequiredDiscordRoleId { get; set; } = 0;

    // Polling Setup
    public ulong LinkChannelId { get; set; } = 0;
    public int PollIntervalSeconds { get; set; } = 20;
    public int VerificationTimeoutMinutes { get; set; } = 3;

    public int GeneratedPasswordLength { get; set; } = 10;
    public bool ShowTemporaryPasswords { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CoreConfig))]
internal partial class MetatronJsonContext : JsonSerializerContext { }
