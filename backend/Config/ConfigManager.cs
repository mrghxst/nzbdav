using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "unknown";

    private readonly Dictionary<string, string> _config = new();

    // Memoizes the values that are expensive to materialize on every read (the JSON-encoded
    // configs). Reads happen on hot paths (e.g. per streamed segment), so we parse once and
    // invalidate whenever the config changes. Guarded by the same lock as `_config`.
    private readonly Dictionary<string, object?> _typedCache = new();

    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            _typedCache.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    private string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue == null ? default : JsonSerializer.Deserialize<T>(rawValue);
    }

    /// <summary>
    /// Returns a memoized, lazily-parsed value for keys whose materialization is expensive
    /// (JSON deserialization). The factory only runs on a cache miss, i.e. once per change.
    /// </summary>
    private T GetCachedValue<T>(string configName, Func<T> factory)
    {
        lock (_config)
        {
            if (_typedCache.TryGetValue(configName, out var cached)) return (T)cached!;
            var value = factory();
            _typedCache[configName] = value;
            return value;
        }
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }

            // Any change invalidates the memoized typed values.
            _typedCache.Clear();
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    /// <summary>
    /// Validates incoming config values, failing fast for anything that would otherwise throw
    /// deep inside a request/background task at read time (non-numeric ints, non-boolean flags,
    /// malformed JSON). Empty values are treated as "unset" and always allowed, matching the
    /// getters' fallback-to-default behavior.
    /// </summary>
    public static void ValidateConfigItems(IEnumerable<ConfigItem> configItems)
    {
        foreach (var item in configItems)
        {
            var value = StringUtil.EmptyToNull(item.ConfigValue);
            if (value == null) continue;

            switch (item.ConfigName)
            {
                case ConfigKeys.MaxDownloadConnections:
                case ConfigKeys.ArticleBufferSize:
                case ConfigKeys.StreamingPriority:
                    RequireInt(item.ConfigName, value);
                    break;

                case ConfigKeys.EnsureImportableVideo:
                case ConfigKeys.ShowHiddenFiles:
                case ConfigKeys.EnforceReadonly:
                case ConfigKeys.PreviewPar2Files:
                case ConfigKeys.IgnoreHistoryLimit:
                case ConfigKeys.RepairEnable:
                case ConfigKeys.RcloneRcEnabled:
                case ConfigKeys.DbStartupVacuumEnabled:
                case ConfigKeys.NzbBackupEnabled:
                case ConfigKeys.RemoveOrphanedScheduleEnabled:
                    RequireBool(item.ConfigName, value);
                    break;

                case ConfigKeys.UsenetProviders:
                    RequireJson<UsenetProviderConfig>(item.ConfigName, value);
                    break;

                case ConfigKeys.ArrInstances:
                    RequireJson<ArrConfig>(item.ConfigName, value);
                    break;
            }
        }

        return;

        static void RequireInt(string key, string value)
        {
            if (!int.TryParse(value, out _))
                throw new ArgumentException($"Config value for '{key}' must be a whole number, but was '{value}'.");
        }

        static void RequireBool(string key, string value)
        {
            if (!bool.TryParse(value, out _))
                throw new ArgumentException($"Config value for '{key}' must be 'true' or 'false', but was '{value}'.");
        }

        static void RequireJson<T>(string key, string value)
        {
            try
            {
                JsonSerializer.Deserialize<T>(value);
            }
            catch (JsonException e)
            {
                throw new ArgumentException($"Config value for '{key}' is not valid JSON: {e.Message}");
            }
        }
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RcloneMountDir))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiKey))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue(ConfigKeys.StrmKey)
               ?? throw new InvalidOperationException($"The `{ConfigKeys.StrmKey}` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.Categories))
                    ?? EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ManualCategory))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavUser))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavPass));
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.EnsureImportableVideo));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ShowHiddenFiles));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.LibraryDir));
    }

    public int GetMaxDownloadConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.MaxDownloadConnections))
            ?? Math.Min(GetUsenetProviderConfig().TotalPooledConnections, 15).ToString()
        );
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ArticleBufferSize))
            ?? "40"
        );
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.StreamingPriority));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.EnforceReadonly));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue(ConfigKeys.EnsureArticleExistenceCategories);
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreviewPar2Files));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.IgnoreHistoryLimit));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairEnable));
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public ArrConfig GetArrConfig()
    {
        return GetCachedValue(ConfigKeys.ArrInstances,
            () => GetConfigValue<ArrConfig>(ConfigKeys.ArrInstances) ?? new ArrConfig());
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        return GetCachedValue(ConfigKeys.UsenetProviders,
            () => GetConfigValue<UsenetProviderConfig>(ConfigKeys.UsenetProviders) ?? new UsenetProviderConfig());
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue(ConfigKeys.DuplicateNzbBehavior) ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue(ConfigKeys.DownloadFileBlocklist) ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue(ConfigKeys.ImportStrategy) ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue(ConfigKeys.CompletedDownloadsDir) ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue(ConfigKeys.BaseUrl) ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RcloneRcEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue(ConfigKeys.RcloneHost);
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue(ConfigKeys.RcloneUser);
    }

    public string? GetRclonePass()
    {
        return GetConfigValue(ConfigKeys.RclonePass);
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UserAgent))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.DbStartupVacuumEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsNzbBackupEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.NzbBackupEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.NzbBackupLocation));
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RemoveOrphanedScheduleEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RemoveOrphanedScheduleTime));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public int GetImportVerificationPercent()
    {
        var defaultValue = 100;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ImportVerificationPercent));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var percent)) return defaultValue;
        return Math.Clamp(percent, 1, 100);
    }

    public int GetHealthCheckVerificationPercent()
    {
        var defaultValue = 100;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairVerificationPercent));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var percent)) return defaultValue;
        return Math.Clamp(percent, 1, 100);
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}
