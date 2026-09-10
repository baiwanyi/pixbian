/**
 * 用户数据备份文档模型（JSON 数据包）。
 * 职责：定义可跨机器迁移的用户数据载体——收藏与评分、收藏分组、分类与规则、条目分类归属、
 *      扫描源与音乐目录；不含媒体文件本身、界面偏好与任何凭据。
 * 复用约定：条目一律以文件路径（media_items.path，业务唯一键）为标识，分类与分组以名称互引——
 *          数据库自增主键在换机或重建索引后必然变化，不可作为跨库标识；
 *          序列化沿用 System.Text.Json，枚举按名称存取以保证文件人类可读。
 * 关键约束：Format 与 FormatVersion 是导入的准入条件，必须先行校验；
 *          密码哈希等凭据严禁进入本模型（DPAPI 保护绑定当前用户与机器，跨机导入必然失效）。
 */

namespace Pixbian.Core.Models;

/// <summary>备份文档的格式标识与版本。</summary>
public static class UserDataBackupFormat
{
    /// <summary>格式名；导入时须严格匹配。</summary>
    public const string Name = "pixbian.userdata";

    /// <summary>当前格式版本；导入拒绝高于本值的文件。</summary>
    public const int CurrentVersion = 1;
}

/// <summary>带评分的收藏条目。</summary>
public sealed record BackupFavorite
{
    /// <summary>文件完整路径（业务唯一键）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>用户评分（0–5）。</summary>
    public int Rating { get; init; }
}

/// <summary>收藏分组及其成员路径。</summary>
public sealed record BackupGroup
{
    /// <summary>分组名称（唯一，合并时的业务键）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>排序序号。</summary>
    public int SortOrder { get; init; }

    /// <summary>创建时间；保留原值以便还原展示顺序与时间信息。</summary>
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>成员文件路径集合。</summary>
    public IReadOnlyList<string> Items { get; init; } = [];
}

/// <summary>分类定义。</summary>
public sealed record BackupCategory
{
    /// <summary>分类名称（唯一，合并时的业务键）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>展示颜色（十六进制）；可为空。</summary>
    public string? Color { get; init; }

    /// <summary>排序序号。</summary>
    public int SortOrder { get; init; }

    /// <summary>是否参与自动匹配。</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>分类规则；所属分类以名称引用同一文档内的分类。</summary>
public sealed record BackupRule
{
    /// <summary>规则名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>正则表达式；导入前必须经 CategoryRuleHelper.ValidatePattern 校验。</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>命中后归入的分类名称。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>匹配目标。</summary>
    public RuleMatchTarget Target { get; init; } = RuleMatchTarget.FileName;

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>优先级，数值越大越先匹配。</summary>
    public int Priority { get; init; }

    /// <summary>是否区分大小写。</summary>
    public bool IsCaseSensitive { get; init; }
}

/// <summary>条目的分类归属；导入时可选择是否应用（规则重匹配会覆盖它）。</summary>
public sealed record BackupItemCategory
{
    /// <summary>文件完整路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>分类名称。</summary>
    public string Category { get; init; } = string.Empty;
}

/// <summary>媒体库扫描源。</summary>
public sealed record BackupLibraryFolder
{
    /// <summary>目录完整路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>展示名称。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>备份文档根对象。</summary>
public sealed record UserDataBackupDocument
{
    /// <summary>格式标识，须等于 <see cref="UserDataBackupFormat.Name"/>。</summary>
    public string Format { get; init; } = UserDataBackupFormat.Name;

    /// <summary>格式版本。</summary>
    public int FormatVersion { get; init; } = UserDataBackupFormat.CurrentVersion;

    /// <summary>导出时的应用版本（仅作溯源展示）。</summary>
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>导出时的数据库 Schema 版本（仅作溯源展示）。</summary>
    public int SchemaVersion { get; init; }

    /// <summary>导出时间（UTC）。</summary>
    public DateTimeOffset ExportedUtc { get; init; }

    /// <summary>导出时的数据规模统计。</summary>
    public UserDataBackupCounts Counts { get; init; } = new();

    /// <summary>收藏条目（只含已收藏的）。</summary>
    public IReadOnlyList<BackupFavorite> Favorites { get; init; } = [];

    /// <summary>收藏分组。</summary>
    public IReadOnlyList<BackupGroup> Groups { get; init; } = [];

    /// <summary>分类。</summary>
    public IReadOnlyList<BackupCategory> Categories { get; init; } = [];

    /// <summary>分类规则。</summary>
    public IReadOnlyList<BackupRule> Rules { get; init; } = [];

    /// <summary>条目的分类归属。</summary>
    public IReadOnlyList<BackupItemCategory> ItemCategories { get; init; } = [];

    /// <summary>媒体库扫描源。</summary>
    public IReadOnlyList<BackupLibraryFolder> LibraryFolders { get; init; } = [];

    /// <summary>音乐库目录（仅该设置字段，不含其它界面偏好）。</summary>
    public IReadOnlyList<string> MusicFolders { get; init; } = [];
}

/// <summary>用户数据的规模统计。</summary>
public sealed record UserDataBackupCounts
{
    /// <summary>收藏条目数。</summary>
    public int Favorites { get; init; }

    /// <summary>收藏分组数。</summary>
    public int Groups { get; init; }

    /// <summary>分类数。</summary>
    public int Categories { get; init; }

    /// <summary>规则数。</summary>
    public int Rules { get; init; }

    /// <summary>扫描源数。</summary>
    public int Folders { get; init; }
}
