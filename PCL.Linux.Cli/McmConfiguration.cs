using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.Shared;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal static class McmConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Dictionary<string, string?> Defaults = new(StringComparer.Ordinal)
    {
        ["game_dir"] = null, ["default_instance"] = null, ["java"] = null, ["username"] = null,
        ["memory.max_mib"] = "auto",
        ["game.auto_language"] = "true", ["game.language"] = "zh-CN",
        ["game.window.mode"] = "default", ["game.window.width"] = "854", ["game.window.height"] = "480",
        ["backup.enabled"] = "false", ["backup.scope"] = "saves", ["backup.retention"] = "5",
        ["tor.enabled"] = "true", ["tor.auto_bridges"] = "true", ["tor.bridges"] = "[]"
    };

    internal static string ConfigPath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "mcm", "config.json");
        }
    }

    internal static string ResolveGameDirectory(string? explicitPath = null) => MinecraftInstanceService.ResolveGameDirectory(
        !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath :
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_GAME_DIR")) ? null : Get("game_dir"));

    internal static JsonObject Load()
    {
        var path = ConfigPath;
        InstanceBackupStore.RequireSafePath(path);
        if (!File.Exists(path)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"MCM config must be a JSON object: {path}");
    }

    private static JsonNode? Lookup(JsonObject root, string key, string? instance)
        => ((root["instances"] as JsonObject)?[instance ?? ""] as JsonObject)?[key] ?? root[key];

    internal static string? Get(string key, string? instance = null) => Get(Load(), key, instance);

    private static string? Get(JsonObject root, string key, string? instance)
    {
        var value = Lookup(root, key, instance);
        return value is JsonValue || key == "tor.bridges" && value is JsonArray ? value.ToString() : Defaults.GetValueOrDefault(key);
    }

    internal static int ResolveMaxHeapMiB(string? setting, long memoryBytes)
    {
        if (setting is null or "auto")
            return memoryBytes > 0 ? (int)Math.Clamp(memoryBytes / (4 * 1024 * 1024), 512, 4096) : 2048;
        if (int.TryParse(setting, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var mib) && mib is >= 512 and <= 65536)
            return mib;
        throw new ArgumentException("Memory must be auto or a whole number between 512 and 65536 MiB.");
    }

    internal static string[] Bridges(string? instance = null)
        => Lookup(Load(), "tor.bridges", instance) is JsonArray values
            ? values.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var bridge) ? bridge : "").Where(b => b.Length > 0).ToArray()
            : [];

    internal static void Save(JsonObject config)
    {
        var path = ConfigPath;
        InstanceBackupStore.RequireSafePath(path);
        config["schema_version"] = 1;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, config, JsonOptions);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static int Run(string[] args)
    {
        var root = Load();
        string? instance = null;
        if (args.Length >= 2 && args[^2] == "--instance")
        {
            instance = args[^1];
            ManagedInstanceService.GetInstanceDirectory(ResolveGameDirectory(), instance);
            args = args[..^2];
        }
        if (args.Length == 0)
        {
            if (Console.IsInputRedirected) return Usage("Interactive config requires a terminal.");
            var target = Scope(root, instance);
            Console.WriteLine("MCM configuration (Enter keeps the current value)");
            foreach (var key in Defaults.Keys.Where(k => k != "tor.bridges" && (instance is null || IsInstanceKey(k))))
            {
                Console.Write($"{key} [{Get(root, key, instance) ?? "unset"}]: ");
                var value = Console.ReadLine();
                if (value is null) return 2;
                if (!string.IsNullOrWhiteSpace(value)) target[key] = ParseValue(key, value);
            }
            Save(root);
            return 0;
        }
        if (args is ["list"])
        {
            foreach (var key in Defaults.Keys) Console.WriteLine($"{key}={Display(root, key, instance)}");
        }
        else if (args is ["get", var key])
        {
            if (!Defaults.ContainsKey(key)) return Usage($"Unknown config key: {key}");
            Console.WriteLine(Display(root, key, instance));
        }
        else if (args is ["set", var setKey, var value])
        {
            if (!Defaults.ContainsKey(setKey) || (instance is not null && !IsInstanceKey(setKey)))
                return Usage($"This configuration scope does not support {setKey}.");
            Scope(root, instance)[setKey] = ParseValue(setKey, value);
            Save(root);
        }
        else if (args[0] == "reset" && args.Length <= 2)
        {
            if (args.Length == 2 && !Defaults.ContainsKey(args[1])) return Usage($"Unknown config key: {args[1]}");
            var target = Scope(root, instance);
            foreach (var name in args.Length == 2 ? [args[1]] : Defaults.Keys.ToArray()) target.Remove(name);
            Save(root);
        }
        else return Usage("Usage: mcm config [list|get <key>|set <key> <value>|reset [key]] [--instance <name>]");
        if (!File.Exists(ConfigPath) || root["schema_version"] is null) Save(root);
        return 0;
    }

    private static string Display(JsonObject root, string key, string? instance) => key == "tor.bridges"
        ? $"{(Lookup(root, key, instance) as JsonArray)?.Count ?? 0} configured"
        : Get(root, key, instance) ?? "";

    private static bool IsInstanceKey(string key) => key is not ("game_dir" or "default_instance");

    private static JsonObject Scope(JsonObject root, string? instance)
    {
        if (instance is null) return root;
        if (root["instances"] is not JsonObject instances) root["instances"] = instances = new JsonObject();
        if (instances[instance] is not JsonObject config) instances[instance] = config = new JsonObject();
        return config;
    }

    internal static GameStartupOptions GetGameStartupOptions(string? instance)
    {
        var root = Load();
        string Value(string key) => Get(root, key, instance)!;
        var language = Value("game.language");
        if (!GameStartupOptionsFile.IsLanguageCode(language))
            throw new ArgumentException("game.language must look like zh-CN or en-US.");
        return new GameStartupOptions(
            bool.TryParse(Value("game.auto_language"), out var autoLanguage)
                ? autoLanguage : throw new ArgumentException("game.auto_language must be true or false."),
            language,
            ParseWindowMode(Value("game.window.mode")),
            ParseWindowDimension("game.window.width", Value("game.window.width")),
            ParseWindowDimension("game.window.height", Value("game.window.height")));
    }

    private static GameStartupWindowMode ParseWindowMode(string mode) => mode switch
    {
        "default" => GameStartupWindowMode.Default,
        "fullscreen" => GameStartupWindowMode.Fullscreen,
        "custom" => GameStartupWindowMode.Custom,
        "maximized" => GameStartupWindowMode.Maximized,
        _ => throw new ArgumentException("game.window.mode must be default, fullscreen, custom or maximized.")
    };

    private static int ParseWindowDimension(string key, string value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var size) && size is >= 100 and <= 16384
            ? size : throw new ArgumentException($"{key} must be a whole number between 100 and 16384.");

    private static JsonNode ParseValue(string key, string value) => key switch
    {
        "backup.enabled" or "tor.enabled" or "tor.auto_bridges" or "game.auto_language" => bool.TryParse(value, out var enabled)
            ? JsonValue.Create(enabled)! : throw new ArgumentException($"{key} must be true or false."),
        "game.language" when !GameStartupOptionsFile.IsLanguageCode(value) =>
            throw new ArgumentException("game.language must look like zh-CN or en-US."),
        "game.window.mode" => JsonValue.Create(ParseWindowMode(value).ToString().ToLowerInvariant())!,
        "game.window.width" or "game.window.height" => JsonValue.Create(ParseWindowDimension(key, value))!,
        "memory.max_mib" => value == "auto" ? JsonValue.Create(value)! : JsonValue.Create(ResolveMaxHeapMiB(value, 0))!,
        "backup.retention" => int.TryParse(value, out var count) && count is >= 1 and <= 100
            ? JsonValue.Create(count)! : throw new ArgumentException("backup.retention must be between 1 and 100."),
        "backup.scope" when value is not ("saves" or "instance") => throw new ArgumentException("backup.scope must be saves or instance."),
        "tor.bridges" => JsonSerializer.SerializeToNode(value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(PCL.Core.Shared.TorNetworkService.ValidateBridge).ToArray())!,
        "game_dir" => JsonValue.Create(Path.GetFullPath(value))!,
        _ => JsonValue.Create(value)!
    };

    private static int Usage(string message) { Console.Error.WriteLine(message); return 2; }
}
