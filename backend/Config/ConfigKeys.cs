namespace NzbWebDAV.Config;

/// <summary>
/// The single source of truth for every configuration key persisted in the
/// <c>ConfigItems</c> table. Using constants instead of scattered string literals
/// means a key can be found (and renamed) from one place, and that the getters in
/// <see cref="ConfigManager"/>, the <c>OnConfigChanged</c> subscribers, and the seed
/// migrations cannot silently drift apart.
/// </summary>
public static class ConfigKeys
{
    // api
    public const string ApiKey = "api.key";
    public const string StrmKey = "api.strm-key";
    public const string Categories = "api.categories";
    public const string ManualCategory = "api.manual-category";
    public const string EnsureImportableVideo = "api.ensure-importable-video";
    public const string EnsureArticleExistenceCategories = "api.ensure-article-existence-categories";
    public const string IgnoreHistoryLimit = "api.ignore-history-limit";
    public const string DuplicateNzbBehavior = "api.duplicate-nzb-behavior";
    public const string DownloadFileBlocklist = "api.download-file-blocklist";
    public const string ImportStrategy = "api.import-strategy";
    public const string CompletedDownloadsDir = "api.completed-downloads-dir";
    public const string UserAgent = "api.user-agent";
    public const string NzbBackupEnabled = "api.nzb-backup-enabled";
    public const string NzbBackupLocation = "api.nzb-backup-location";
    public const string ImportVerificationPercent = "api.import-verification-percent";

    // usenet
    public const string UsenetProviders = "usenet.providers";
    public const string MaxDownloadConnections = "usenet.max-download-connections";
    public const string ArticleBufferSize = "usenet.article-buffer-size";
    public const string StreamingPriority = "usenet.streaming-priority";

    // webdav
    public const string WebdavUser = "webdav.user";
    public const string WebdavPass = "webdav.pass";
    public const string ShowHiddenFiles = "webdav.show-hidden-files";
    public const string EnforceReadonly = "webdav.enforce-readonly";
    public const string PreviewPar2Files = "webdav.preview-par2-files";

    // media / repair / arr
    public const string LibraryDir = "media.library-dir";
    public const string RepairEnable = "repair.enable";
    public const string RepairVerificationPercent = "repair.verification-percent";
    public const string ArrInstances = "arr.instances";

    // rclone
    public const string RcloneMountDir = "rclone.mount-dir";
    public const string RcloneRcEnabled = "rclone.rc-enabled";
    public const string RcloneHost = "rclone.host";
    public const string RcloneUser = "rclone.user";
    public const string RclonePass = "rclone.pass";

    // general / db / maintenance
    public const string BaseUrl = "general.base-url";
    public const string DbStartupVacuumEnabled = "db.is-startup-vacuum-enabled";
    public const string RemoveOrphanedScheduleEnabled = "maintenance.remove-orphaned-schedule-enabled";
    public const string RemoveOrphanedScheduleTime = "maintenance.remove-orphaned-schedule-time";
}
