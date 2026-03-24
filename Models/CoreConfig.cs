namespace Metatron;

public class CoreConfig
{
    public bool EnableDiscordGate { get; set; } = true;
    public bool EnableFrictionlessAuth { get; set; } = true;
    public string DiscordBotToken { get; set; } = "";
    public ulong DiscordGuildId { get; set; } = 0;
    public ulong RequiredDiscordRoleId { get; set; } = 0;
    public ulong LinkChannelId { get; set; } = 0;
    public int PollIntervalSeconds { get; set; } = 20;
    public int VerificationTimeoutMinutes { get; set; } = 3;
    public bool ShowTemporaryPasswords { get; set; } = true;
    public int LedgerAuditIntervalHours { get; set; } = 24;
    
    public MetatronStrings Strings { get; set; } = new();
}

public class MetatronStrings
{
    public string LimboMessage { get; set; } = "[c/FF0000:Discord Gate Active:] Type [c/00FF00:/verify <pin>] to enter.";
    public string VerifySuccess { get; set; } = "✨ Verification Complete. Welcome to the realm.";
    public string DiscordBroadcast { get; set; } = "✨ <@{0}> has forged their seal as `{1}`!";
}
