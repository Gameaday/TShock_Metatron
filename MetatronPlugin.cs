using System;
using System.IO;
using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

#nullable enable

namespace Metatron;

[ApiVersion(2, 1)]
public class MetatronPlugin : TerrariaPlugin
{
    public override string Name => "Project Metatron";
    public override Version Version => new Version(3, 0, 0);
    public override string Author => "HistoryLabs";

    public static bool IsShuttingDown = false;
    
    private CoreConfig _config = new();
    private DatabaseService? _database;
    private DiscordService? _discord;
    private GatekeeperService? _gatekeeper;

    static MetatronPlugin()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string name = new AssemblyName(args.Name).Name ?? "";
            if (!name.StartsWith("Discord") && !name.StartsWith("System.Interactive") && !name.StartsWith("System.Linq") && !name.StartsWith("Microsoft.Bcl"))
                return null;

            string resourceName = $"Metatron.Resources.{name}.dll";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Assembly.Load(ms.ToArray());
        };
    }

    public MetatronPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        LoadConfig();

        _database = new DatabaseService();
        _discord = new DiscordService(_config, _database);
        _gatekeeper = new GatekeeperService(this, _config, _database, _discord);

        _database.Initialize();
        if (_config.EnableDiscordGate)
        {
            _gatekeeper.EnableHooks();
            _ = _discord.StartAsync();
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => {
            IsShuttingDown = true;
            _discord?.Stop();
        };

        Commands.ChatCommands.Add(new Command("metatron.admin", AdminCommand, "metatron", "meta"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsShuttingDown = true;
            _gatekeeper?.DisableHooks();
            _discord?.Stop();
        }
        base.Dispose(disposing);
    }

    private void LoadConfig()
    {
        string path = Path.Combine(TShock.SavePath, "Metatron", "Core.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                _config = System.Text.Json.JsonSerializer.Deserialize<CoreConfig>(text, MetatronJsonContext.Default.CoreConfig) ?? new CoreConfig();
            }
            else
            {
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(_config, MetatronJsonContext.Default.CoreConfig));
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Metatron] Config load failed: {ex.Message}"); }
    }

    private void AdminCommand(CommandArgs args)
    {
        if (args.Parameters.Count == 0) { args.Player.SendErrorMessage("Usage: /meta <reload | check | unlink | whois>"); return; }
        string cmd = args.Parameters[0].ToLower();

        if (cmd == "reload") { LoadConfig(); args.Player.SendSuccessMessage("[Metatron] Core Config reloaded."); }
        else if (cmd == "whois" && args.Parameters.Count > 1)
        {
            string target = string.Join(" ", args.Parameters.Skip(1)).ToLower();
            if (_database?.Ledger.TryGetValue(target, out var record) == true)
                args.Player.SendInfoMessage($"🔍 [Metatron] Identity: Discord {record.DiscordId} | UUID {record.Uuid}");
            else args.Player.SendErrorMessage($"[Metatron] No record found for '{target}'.");
        }
        else if (cmd == "unlink" && args.Parameters.Count > 1)
        {
            string targetName = string.Join(" ", args.Parameters.Skip(1)).ToLower();
            if (_database?.Ledger.TryRemove(targetName, out var record) == true) {
                _ = _database.RemoveSealAsync(record.DiscordId);
                args.Player.SendSuccessMessage($"[Metatron] Severed seal for {targetName}.");
                TShock.Players.FirstOrDefault(p => p?.Account?.Name.ToLower() == targetName)?.Disconnect("Your Discord seal was severed.");
            }
        }
        else if (cmd == "check") args.Player.SendInfoMessage($"Discord Gate: {(_config.EnableDiscordGate ? "ON" : "OFF")}");
    }
}
