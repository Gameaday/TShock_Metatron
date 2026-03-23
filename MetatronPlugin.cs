extern alias BCryptNet;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

#nullable enable

namespace Metatron;

[ApiVersion(2, 1)]
public partial class MetatronPlugin : TerrariaPlugin
{
    public override string Name => "Project Metatron";
    public override Version Version => new Version(2, 3, 0);
    public override string Author => "HistoryLabs";

    private CoreConfig _config = new();
    private List<Broadcast> _allBroadcasts = new();
    public static bool IsStreaming = false;
    public static bool IsShuttingDown = false; // KILL SWITCH

    // In-Memory Ledgers
    private readonly ConcurrentDictionary<string, MetatronRecord> _ledger = new();
    private readonly ConcurrentDictionary<string, (ulong DiscordId, DateTime Expiry)> _pendingPins = new();
    private readonly ConcurrentDictionary<string, string> _pendingPasswords = new();
    private readonly ConcurrentDictionary<int, DateTime> _limboPlayers = new();
    
    // Security & Stability
    private readonly ConcurrentDictionary<ulong, DateTime> _scribeRateLimit = new();
    private readonly ConcurrentDictionary<string, int> _loginStrikes = new();
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    private static readonly char[] PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();
    private static readonly HttpClient _httpClient = new();
    private bool _wasDayTime;

    public MetatronPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        InitializePersistence();
        InitializeGatekeeperEnforcement();
        
        if (_config.EnableDiscordGate) _ = InitializeDiscordRestAsync();

        _wasDayTime = Main.dayTime;

        // --- DOCKER GRACEFUL SHUTDOWN HOOK ---
        AppDomain.CurrentDomain.ProcessExit += (s, e) => 
        {
            IsShuttingDown = true;
            if (_discordTimer != null)
            {
                _discordTimer.Stop();
                _discordTimer.Dispose();
            }

            TShock.Log.ConsoleWarn("[Metatron] Docker Signal (SIGTERM) detected. Initiating graceful save...");
            TShock.Utils.StopServer(true, "Docker Shutdown Signal Received.");
        };
        
        ServerApi.Hooks.NetGetData.Register(this, OnGatekeeperPassword, 100); 
        ServerApi.Hooks.ServerJoin.Register(this, OnGatekeeperJoin, int.MaxValue);
        ServerApi.Hooks.NetGreetPlayer.Register(this, OnGatekeeperGreet);
        ServerApi.Hooks.ServerLeave.Register(this, OnGatekeeperLeave);
        ServerApi.Hooks.GameUpdate.Register(this, OnAnnouncerPulse);

        if (_config.EnableBroadcaster)
        {
            ServerApi.Hooks.ServerChat.Register(this, OnAnnouncerChat);
            ServerApi.Hooks.NetGetData.Register(this, OnAnnouncerGetData);
            ServerApi.Hooks.NpcKilled.Register(this, OnAnnouncerNpcKilled);
        }

        Commands.ChatCommands.Add(new Command("metatron.admin", AdminCommand, "metatron", "meta"));
        Commands.ChatCommands.Add(new Command("", VerifyCommand, "verify"));
        Commands.ChatCommands.Add(new Command("", UnlinkCommand, "unlink"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsShuttingDown = true;

            if (_config.EnableDiscordGate && _discordRest != null)
            {
                try { UpdateStatusMessageAsync(false).GetAwaiter().GetResult(); } catch { }
            }

            if (_discordTimer != null)
            {
                _discordTimer.Stop();
                _discordTimer.Dispose();
            }

            _configWatcher?.Dispose();
            
            ServerApi.Hooks.NetGetData.Deregister(this, OnGatekeeperPassword);
            ServerApi.Hooks.ServerJoin.Deregister(this, OnGatekeeperJoin);
            ServerApi.Hooks.NetGreetPlayer.Deregister(this, OnGatekeeperGreet);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnGatekeeperLeave);
            
            if (_config.EnableBroadcaster)
            {
                ServerApi.Hooks.ServerChat.Deregister(this, OnAnnouncerChat);
                ServerApi.Hooks.NetGetData.Deregister(this, OnAnnouncerGetData);
                ServerApi.Hooks.NpcKilled.Deregister(this, OnAnnouncerNpcKilled);
            }
            
            ServerApi.Hooks.GameUpdate.Deregister(this, OnAnnouncerPulse);
        }
        base.Dispose(disposing);
    }

    private void AdminCommand(CommandArgs args)
    {
        if (args.Parameters.Count == 0)
        {
            args.Player.SendErrorMessage("Usage: /meta <reload | check | live | unlink | whois>");
            return;
        }

        string cmd = args.Parameters[0].ToLower();

        if (cmd == "reload")
        {
            LoadCoreConfig();
            LoadBroadcastLibrary();
            args.Player.SendSuccessMessage("[Metatron] Configs safely reloaded.");
        }
        else if (cmd == "live")
        {
            IsStreaming = !IsStreaming;
            args.Player.SendSuccessMessage($"[Metatron] Streamer Mode: {(IsStreaming ? "ON" : "OFF")}");
            if (IsStreaming && _config.StreamAnnouncementChannelId != 0)
                _ = PostDiscordMessageRestAsync(_config.StreamAnnouncementChannelId, $"🔴 **The Stream is LIVE!** Watch here: {_config.StreamUrl}");
        }
        else if (cmd == "whois" && args.Parameters.Count > 1)
        {
            string target = string.Join(" ", args.Parameters.Skip(1)).ToLower();
            if (_ledger.TryGetValue(target, out var record))
            {
                args.Player.SendInfoMessage($"🔍 [Metatron] Identity Resolution for {target}:");
                _ = Task.Run(async () => {
                    string discordName = await GetDiscordNameAsync(record.DiscordId);
                    args.Player.SendSuccessMessage($" > Discord: {discordName}");
                    args.Player.SendInfoMessage($" > UUID: {record.Uuid}");
                });
            }
            else args.Player.SendErrorMessage($"[Metatron] No record found for '{target}'.");
        }
        else if (cmd == "unlink" && args.Parameters.Count > 1)
        {
            string targetName = string.Join(" ", args.Parameters.Skip(1)).ToLower();
            if (_ledger.TryRemove(targetName, out var record))
            {
                RemoveSealFromDatabase(record.DiscordId);
                args.Player.SendSuccessMessage($"[Metatron] Severed seal for {targetName}.");
                TShock.Players.FirstOrDefault(p => p?.Account?.Name.ToLower() == targetName)?.Disconnect("Your Discord seal was severed.");
            }
        }
        else if (cmd == "check")
        {
            args.Player.SendInfoMessage("=== Metatron REST Diagnostics ===");
            args.Player.SendInfoMessage($"Discord Gate: {(_config.EnableDiscordGate ? "ON" : "OFF")}");
            args.Player.SendInfoMessage($"Active Quarantines: {_limboPlayers.Count}");
        }
    }
}