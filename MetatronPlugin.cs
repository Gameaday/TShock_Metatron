extern alias BCryptNet;

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

    static MetatronPlugin()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string name = new AssemblyName(args.Name).Name ?? "";
            if (!name.StartsWith("Discord") && !name.StartsWith("BCrypt") && !name.StartsWith("System.Interactive") && !name.StartsWith("System.Linq") && !name.StartsWith("Microsoft.Bcl"))
                return null;

            string resourceName = $"Metatron.Resources.{name}.dll";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            byte[] data = new byte[stream.Length];
            stream.Read(data, 0, data.Length);
            return Assembly.Load(data);
        };
    }

    public MetatronPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        LoadConfig();

        _database = new DatabaseService();
        _discord = new DiscordService(_config, _database);

        _database.Initialize();
        if (_config.EnableDiscordGate)
        {
            _ = _discord.StartAsync();
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => {
            IsShuttingDown = true;
            _discord?.Stop();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsShuttingDown = true;
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
}
