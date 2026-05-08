using Tomlyn;

namespace Omicron.Core.Config;

// ==============================================================
// TOML-serializable model — sections map directly to TOML tables
// ==============================================================

/// <summary>
/// Root TOML config file structure. Tomlyn deserializes into this.
/// </summary>
public class ConfigFile
{
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public GeneralSettings General { get; set; } = new();
}

public class GeneralSettings
{
    public string? LastModel { get; set; }
    public int DefaultMaxTokens { get; set; } = 16384;
    public double DefaultTemperature { get; set; } = 0.7;
    public string? SystemPrompt { get; set; }
    public int DisplayLineWidth { get; set; } = 120;
    public int DisplayMaxLines { get; set; } = 60;
    public int MaxIterations { get; set; } = 100;
}

/// <summary>
/// Simplified runtime config (flat, with helpers).
/// </summary>
public class AgentConfig
{
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastModel { get; set; }
    public int DefaultMaxTokens { get; set; } = 16384;
    public double DefaultTemperature { get; set; } = 0.7;
    public string? SystemPrompt { get; set; }
    public int DisplayLineWidth { get; set; } = 120;
    public int DisplayMaxLines { get; set; } = 60;
    public int MaxIterations { get; set; } = 100;
}

/// <summary>
/// Manages reading/writing the TOML config file.
/// Stored at: %APPDATA%/Omicron/config.toml (Windows) or ~/.config/omicron/config.toml (Unix)
/// </summary>
public class ConfigManager
{
    private readonly string _configDir;
    private readonly string _configPath;

    public AgentConfig Config { get; private set; } = new();

    public ConfigManager()
    {
        _configDir = GetConfigDirectory();
        _configPath = Path.Combine(_configDir, "config.toml");
    }

    public ConfigManager(string configPath)
    {
        _configPath = configPath;
        _configDir = Path.GetDirectoryName(configPath)!;
    }

    public string GetConfigPath() => _configPath;

    /// <summary>
    /// Load config from disk using Tomlyn's typed deserializer.
    /// </summary>
    public void Load()
    {
        if (!File.Exists(_configPath))
        {
            Config = new AgentConfig();
            return;
        }

        try
        {
            var toml = File.ReadAllText(_configPath);
            var file = TomlSerializer.Deserialize<ConfigFile>(toml);
            if (file is null) { Config = new AgentConfig(); return; }

            Config = new AgentConfig
            {
                ApiKeys = file.ApiKeys ?? new(StringComparer.OrdinalIgnoreCase),
                LastModel = file.General?.LastModel,
                DefaultMaxTokens = file.General?.DefaultMaxTokens ?? 16384,
                DefaultTemperature = file.General?.DefaultTemperature ?? 0.7,
                SystemPrompt = file.General?.SystemPrompt,
                DisplayLineWidth = file.General?.DisplayLineWidth ?? 120,
                DisplayMaxLines = file.General?.DisplayMaxLines ?? 60,
                MaxIterations = file.General?.MaxIterations ?? 100
            };
        }
        catch
        {
            Config = new AgentConfig();
        }
    }

    /// <summary>
    /// Save config to disk using Tomlyn's typed serializer.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(_configDir);

        var file = new ConfigFile
        {
            ApiKeys = Config.ApiKeys,
            General = new GeneralSettings
            {
                LastModel = Config.LastModel,
                DefaultMaxTokens = Config.DefaultMaxTokens,
                DefaultTemperature = Config.DefaultTemperature,
                SystemPrompt = Config.SystemPrompt,
                DisplayLineWidth = Config.DisplayLineWidth,
                DisplayMaxLines = Config.DisplayMaxLines,
                MaxIterations = Config.MaxIterations
            }
        };

        var toml = TomlSerializer.Serialize(file);
        File.WriteAllText(_configPath, toml);
    }

    /// <summary>
    /// Get an API key: first check config, then env var.
    /// </summary>
    public string? GetApiKey(string providerName, string envVarName)
    {
        if (Config.ApiKeys.TryGetValue(providerName, out var key) && !string.IsNullOrEmpty(key))
            return key;

        var env = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrEmpty(env)) return env;

        env = Environment.GetEnvironmentVariable(envVarName, EnvironmentVariableTarget.User);
        return !string.IsNullOrEmpty(env) ? env : null;
    }

    /// <summary>
    /// Set an API key for a provider and persist to disk.
    /// </summary>
    public void SetApiKey(string providerName, string key)
    {
        Config.ApiKeys[providerName] = key;
        Save();
    }

    private static string GetConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            return Path.Combine(appData, "Omicron");

        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? ".";
        return Path.Combine(home, ".config", "omicron");
    }
}
