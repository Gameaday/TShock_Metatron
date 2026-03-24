extern alias BCryptNet;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

#nullable enable

namespace Metatron;

[ApiVersion(2, 1)]
public partial class MetatronPlugin : TerrariaPlugin
{
    public override string Name => "Project Metatron";
    public override Version Version => new Version(3, 0, 0);
    public override string Author => "HistoryLabs";

    private CoreConfig _config = new();
    public static bool IsShuttingDown = false; 

    private readonly ConcurrentDictionary<string, MetatronRecord> _ledger = new();
    private readonly ConcurrentDictionary<string, (ulong DiscordId, DateTime Expiry)> _pendingPins = new();
    private readonly ConcurrentDictionary<string, string> _pendingPasswords = new();
    private readonly ConcurrentDictionary<int, DateTime> _limboPlayers = new();
    
    private readonly ConcurrentDictionary<ulong, DateTime> _scribeRateLimit = new();
    private readonly ConcurrentDictionary<string, int> _loginStrikes = new();
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    private static readonly HttpClient _httpClient = new();

    static MetatronPlugin()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string name = new System.Reflection.AssemblyName(args.Name).Name ?? "";
            if (!name.StartsWith("Discord") && !name.StartsWith("BCrypt") && !name.StartsWith("System.Interactive") && !name.StartsWith("System.Linq") && !name.StartsWith("Microsoft.Bcl"))
                return null;

            string resourceName = $"Metatron.Resources.{name}.dll";
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            byte[] data = new byte[stream.Length];
            stream.Read(data, 0, data.Length);
            return System.Reflection.Assembly.Load(data);
        };
    }

    public MetatronPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        InitializePersistence();
        InitializeGatekeeperEnforcement();
        
        if (_config.EnableDiscordGate) _ = InitializeDiscordRestAsync();

        AppDomain.CurrentDomain.ProcessExit += (s, e) => {
            IsShuttingDown = true;
            _discordTimer?.Stop();
        };
        
        ServerApi.Hooks.NetGetData.Register(this, OnGatekeeperPassword, 100); 
        ServerApi.Hooks.ServerJoin.Register(this, OnGatekeeperJoin, int.MaxValue);
        ServerApi.Hooks.NetGreetPlayer.Register(this, OnGatekeeperGreet);
        ServerApi.Hooks.ServerLeave.Register(this, OnGatekeeperLeave);
        ServerApi.Hooks.GameUpdate.Register(this, OnGatekeeperPulse);

        Commands.ChatCommands.Add(new Command("metatron.admin", AdminCommand, "metatron", "meta"));
        Commands.ChatCommands.Add(new Command("", VerifyCommand, "verify"));
        Commands.ChatCommands.Add(new Command("", UnlinkCommand, "unlink"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsShuttingDown = true;
            if (_config.EnableDiscordGate && _discordRest != null) try { UpdateStatusMessageAsync(false).GetAwaiter().GetResult(); } catch { }
            _discordTimer?.Stop();
            _configWatcher?.Dispose();
            
            ServerApi.Hooks.NetGetData.Deregister(this, OnGatekeeperPassword);
            ServerApi.Hooks.ServerJoin.Deregister(this, OnGatekeeperJoin);
            ServerApi.Hooks.NetGreetPlayer.Deregister(this, OnGatekeeperGreet);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnGatekeeperLeave);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGatekeeperPulse);
        }
        base.Dispose(disposing);
    }

    private void AdminCommand(CommandArgs args)
    {
        if (args.Parameters.Count == 0) { args.Player.SendErrorMessage("Usage: /meta <reload | check | unlink | whois>"); return; }
        string cmd = args.Parameters[0].ToLower();

        if (cmd == "reload") { LoadCoreConfig(); args.Player.SendSuccessMessage("[Metatron] Core Config reloaded."); }
        else if (cmd == "whois" && args.Parameters.Count > 1)
        {
            string target = string.Join(" ", args.Parameters.Skip(1)).ToLower();
            if (_ledger.TryGetValue(target, out var record))
                args.Player.SendInfoMessage($"🔍 [Metatron] Identity: Discord {record.DiscordId} | UUID {record.Uuid}");
            else args.Player.SendErrorMessage($"[Metatron] No record found for '{target}'.");
        }
        else if (cmd == "unlink" && args.Parameters.Count > 1)
        {
            string targetName = string.Join(" ", args.Parameters.Skip(1)).ToLower();
            if (_ledger.TryRemove(targetName, out var record)) {
                RemoveSealFromDatabase(record.DiscordId);
                args.Player.SendSuccessMessage($"[Metatron] Severed seal for {targetName}.");
                TShock.Players.FirstOrDefault(p => p?.Account?.Name.ToLower() == targetName)?.Disconnect("Your Discord seal was severed.");
            }
        }
        else if (cmd == "check") args.Player.SendInfoMessage($"Discord Gate: {(_config.EnableDiscordGate ? "ON" : "OFF")} | Quarantines: {_limboPlayers.Count}");
    }
}
