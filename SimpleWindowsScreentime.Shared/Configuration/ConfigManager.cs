using System.Text.Json;

namespace SimpleWindowsScreentime.Shared.Configuration;

public class ConfigManager
{
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private AppConfig _cachedConfig;
    private DateTime _lastSaveTime = DateTime.MinValue;

    public ConfigManager()
    {
        _cachedConfig = Load();
    }

    public AppConfig Config => _cachedConfig;

    public AppConfig Load()
    {
        lock (_lock)
        {
            try
            {
                EnsureDirectoryExists();

                if (File.Exists(Constants.ConfigFilePath))
                {
                    var json = File.ReadAllText(Constants.ConfigFilePath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        _cachedConfig = config;
                        return config;
                    }
                }
            }
            catch
            {
                // Config corrupted or unreadable, return default
            }

            _cachedConfig = AppConfig.CreateDefault();
            return _cachedConfig;
        }
    }

    public void Save(AppConfig? config = null)
    {
        lock (_lock)
        {
            if (config != null)
            {
                _cachedConfig = config;
            }

            try
            {
                EnsureDirectoryExists();
                var json = JsonSerializer.Serialize(_cachedConfig, _jsonOptions);
                File.WriteAllText(Constants.ConfigFilePath, json);
                _lastSaveTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to save configuration: {ex.Message}", ex);
            }
        }
    }

    public void Update(Action<AppConfig> updateAction)
    {
        lock (_lock)
        {
            updateAction(_cachedConfig);
            Save();
        }
    }

    public bool ConfigFileExists()
    {
        return File.Exists(Constants.ConfigFilePath);
    }

    public void EnsureConfigFileExists()
    {
        if (!ConfigFileExists())
        {
            Save();
        }
    }

    public void RewriteFromCache()
    {
        Save();
    }

    public void Reset()
    {
        lock (_lock)
        {
            _cachedConfig = AppConfig.CreateDefault();
            Save();
        }
    }

    private static void EnsureDirectoryExists()
    {
        var directory = Path.GetDirectoryName(Constants.ConfigFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public static bool IsConfigCorrupted()
    {
        try
        {
            if (!File.Exists(Constants.ConfigFilePath))
                return false;

            var json = File.ReadAllText(Constants.ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json);
            return config == null;
        }
        catch
        {
            return true;
        }
    }
}
