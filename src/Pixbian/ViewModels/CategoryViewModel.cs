/**
 * 分类规则管理视图模型。
 * 职责：管理分类与规则的增删改查、启停，以及批量重新匹配；
 *      新建与编辑共用一套表单草稿，按编辑目标主键分流保存。
 *      优先级调整已实现（ReorderRulesAsync），但当前无界面入口，属未接线能力。
 * 复用约定：批量匹配走 CategoryRuleEngine，结果经仓储批量写回；
 *          所有集合修改一律切回 UI 线程执行（仓储内部使用 ConfigureAwait(false)）。
 * 关键约束：新增或修改规则前必须先校验正则——语法非法或存在性能风险的规则不得入库；
 *          分类名称入库前必须校验重名（名称唯一约束，编辑时排除自身）；
 *          禁用分类仅使其下规则退出匹配（仓储层 join 过滤），不改动已有归属；
 *          批量重匹配分批处理（分页读取 + 逐页写回），当前无用户取消入口，
 *          超时兜底只来自整批 30 秒总时限（CategoryRuleEngine）。
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Services;

namespace Pixbian.ViewModels;

/// <summary>规则条目的界面展示包装：附所属分类名，供列表直接绑定。</summary>
public sealed record RuleDisplay(CategoryRule Rule, string CategoryName);

/// <summary>分类规则管理视图模型。</summary>
public sealed partial class CategoryViewModel : ObservableObject
{
    private const int MatchPageSize = 1000;

    private readonly ICategoryRepository _categories;
    private readonly ICategoryRuleRepository _rules;
    private readonly IMediaItemRepository _mediaItems;
    private readonly DispatcherQueue _dispatcherQueue;

    private bool _isApplying;

    /// <summary>正在编辑的规则主键；null 表示新建模式。</summary>
    public long? EditingRuleId { get; private set; }

    /// <summary>正在编辑的分类主键；null 表示新建模式。</summary>
    public long? EditingCategoryId { get; private set; }

    private CategoryRule? _editingRule;

    [ObservableProperty]
    private string _statusText = "就绪";

    // 行级动态说明：设置页「分类管理 / 规则管理」两行副标题分别承载各自计数。
    [ObservableProperty]
    private string _categoryCountText = "共 0 个分类";

    [ObservableProperty]
    private string _ruleCountText = "共 0 条规则";

    [ObservableProperty]
    private bool _isBusy;

    // 新建/编辑规则的表单草稿；对话框打开时注入初值，确认时按 EditingRuleId 分流保存。
    [ObservableProperty]
    private string _newRuleName = string.Empty;

    [ObservableProperty]
    private string _newRulePattern = string.Empty;

    [ObservableProperty]
    private long _newRuleCategoryId;

    [ObservableProperty]
    private RuleMatchTarget _newRuleTarget = RuleMatchTarget.FileName;

    [ObservableProperty]
    private bool _newRuleCaseSensitive;

    // 新建/编辑分类的名称草稿。
    [ObservableProperty]
    private string _newCategoryName = string.Empty;

    public CategoryViewModel(
        ICategoryRepository categories,
        ICategoryRuleRepository rules,
        IMediaItemRepository mediaItems,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(mediaItems);

        _categories = categories;
        _rules = rules;
        _mediaItems = mediaItems;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>分类集合。</summary>
    public ObservableCollection<Category> Categories { get; } = [];

    /// <summary>规则集合（附所属分类名的展示包装）。</summary>
    public ObservableCollection<RuleDisplay> Rules { get; } = [];

    /// <summary>是否已有可归类的分类。</summary>
    public bool HasCategories => Categories.Count > 0;

    /// <summary>规则表单的正则是否合法且已选分类。</summary>
    public bool IsNewRuleValid =>
        CategoryRuleHelper.ValidatePattern(NewRulePattern).IsValid && NewRuleCategoryId > 0;

    /// <summary>规则表单的校验提示。</summary>
    public string NewRuleValidationText
    {
        get
        {
            if (NewRuleCategoryId <= 0)
            {
                return "请先选择目标分类。";
            }

            var result = CategoryRuleHelper.ValidatePattern(NewRulePattern);
            return result.IsValid ? "正则可用。" : result.ErrorMessage;
        }
    }

    /// <summary>分类名称是否可用：非空且不与其它分类重名（编辑时排除自身）。</summary>
    public bool IsNewCategoryNameValid =>
        !string.IsNullOrWhiteSpace(NewCategoryName)
        && Categories.All(c => c.Id == EditingCategoryId || c.Name != NewCategoryName.Trim());

    /// <summary>分类名称的校验提示。</summary>
    public string NewCategoryValidationText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewCategoryName))
            {
                return "请输入分类名称。";
            }

            return IsNewCategoryNameValid ? "名称可用。" : "名称已存在。";
        }
    }

    /// <summary>通知界面校验文本需要刷新（规则与分类两侧）。</summary>
    public void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(IsNewRuleValid));
        OnPropertyChanged(nameof(NewRuleValidationText));
        OnPropertyChanged(nameof(IsNewCategoryNameValid));
        OnPropertyChanged(nameof(NewCategoryValidationText));
    }

    /// <summary>加载分类与规则。</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var categories = await _categories.GetAllAsync();
        var rules = await _rules.GetAllAsync();

        var nameById = categories.ToDictionary(c => c.Id, c => c.Name);

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            Categories.Clear();

            foreach (var category in categories)
            {
                Categories.Add(category);
            }

            Rules.Clear();

            foreach (var rule in rules)
            {
                Rules.Add(new RuleDisplay(
                    rule,
                    nameById.GetValueOrDefault(rule.CategoryId, "未知分类")));
            }

            OnPropertyChanged(nameof(HasCategories));
            CategoryCountText = $"共 {Categories.Count} 个分类";
            RuleCountText = $"共 {Rules.Count} 条规则";
        });
    }

    /// <summary>进入新建分类模式，清空名称草稿。</summary>
    public void BeginNewCategory()
    {
        EditingCategoryId = null;
        NewCategoryName = string.Empty;
        NotifyValidationChanged();
    }

    /// <summary>进入编辑分类模式，用既有值填充名称草稿。</summary>
    public void BeginEditCategory(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);

        EditingCategoryId = category.Id;
        NewCategoryName = category.Name;
        NotifyValidationChanged();
    }

    /// <summary>保存分类表单：无编辑目标时新增，否则改名。</summary>
    [RelayCommand(CanExecute = nameof(IsNewCategoryNameValid))]
    public async Task SaveCategoryAsync()
    {
        if (EditingCategoryId is long id)
        {
            var source = Categories.First(c => c.Id == id);
            await _categories.UpdateAsync(source with { Name = NewCategoryName.Trim() });
            StatusText = "分类已更新。";
        }
        else
        {
            var created = await _categories.AddAsync(NewCategoryName.Trim());

            // 新建后默认选中该分类，减少「建完分类还要在规则表单里找」的步骤。
            NewRuleCategoryId = created.Id;
            OnPropertyChanged(nameof(NewRuleCategoryId));
            StatusText = "分类已添加。";
        }

        NewCategoryName = string.Empty;
        EditingCategoryId = null;
        NotifyValidationChanged();

        await LoadAsync();
    }

    /// <summary>切换分类的启用状态；禁用只影响后续匹配，不动已有归属。</summary>
    [RelayCommand]
    public async Task ToggleCategoryAsync(Category? category)
    {
        if (category is null)
        {
            return;
        }

        await _categories.SetEnabledAsync(category.Id, !category.IsEnabled);
        await LoadAsync();
    }

    /// <summary>删除分类；其下规则由数据库级联删除。</summary>
    [RelayCommand]
    public async Task DeleteCategoryAsync(Category? category)
    {
        if (category is null)
        {
            return;
        }

        await _categories.DeleteAsync(category.Id);
        await LoadAsync();
        StatusText = $"分类「{category.Name}」及其规则已删除。";
    }

    /// <summary>进入新建规则模式，清空表单草稿并默认选中第一个分类。</summary>
    public void BeginNewRule()
    {
        EditingRuleId = null;
        _editingRule = null;
        NewRuleName = string.Empty;
        NewRulePattern = string.Empty;
        NewRuleCategoryId = Categories.Count > 0 ? Categories[0].Id : 0;
        NewRuleTarget = RuleMatchTarget.FileName;
        NewRuleCaseSensitive = false;
        OnPropertyChanged(nameof(NewRuleCategoryId));
        NotifyValidationChanged();
    }

    /// <summary>进入编辑规则模式，用既有值填充表单草稿。</summary>
    public void BeginEditRule(CategoryRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        EditingRuleId = rule.Id;
        _editingRule = rule;
        NewRuleName = rule.Name;
        NewRulePattern = rule.Pattern;
        NewRuleCategoryId = rule.CategoryId;
        NewRuleTarget = rule.Target;
        NewRuleCaseSensitive = rule.IsCaseSensitive;
        OnPropertyChanged(nameof(NewRuleCategoryId));
        NotifyValidationChanged();
    }

    /// <summary>保存规则表单：无编辑目标时新增，否则按原启停与优先级更新。</summary>
    [RelayCommand(CanExecute = nameof(IsNewRuleValid))]
    public async Task SaveRuleAsync()
    {
        if (!IsNewRuleValid)
        {
            StatusText = "正则不可用，请先修正。";
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewRuleName) ? NewRulePattern : NewRuleName.Trim();

        if (EditingRuleId is long id && _editingRule is not null)
        {
            await _rules.UpdateAsync(_editingRule with
            {
                Name = name,
                Pattern = NewRulePattern.Trim(),
                CategoryId = NewRuleCategoryId,
                Target = NewRuleTarget,
                IsCaseSensitive = NewRuleCaseSensitive
            });
            StatusText = "规则已更新。";
        }
        else
        {
            await _rules.AddAsync(new CategoryRule
            {
                Name = name,
                Pattern = NewRulePattern.Trim(),
                CategoryId = NewRuleCategoryId,
                Target = NewRuleTarget,
                IsCaseSensitive = NewRuleCaseSensitive,
                IsEnabled = true,
                Priority = 0
            });
            StatusText = "规则已添加。";
        }

        EditingRuleId = null;
        _editingRule = null;
        NotifyValidationChanged();

        await LoadAsync();
    }

    /// <summary>切换规则的启用状态。</summary>
    /// <param name="rule">目标规则。</param>
    [RelayCommand]
    public async Task ToggleRuleAsync(CategoryRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        await _rules.SetEnabledAsync(rule.Id, !rule.IsEnabled);
        await LoadAsync();
    }

    /// <summary>删除规则。</summary>
    /// <param name="rule">目标规则。</param>
    [RelayCommand]
    public async Task DeleteRuleAsync(CategoryRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        await _rules.DeleteAsync(rule.Id);
        await LoadAsync();
    }

    /// <summary>把规则在列表中的位置变化写回为优先级。</summary>
    /// <param name="orderedRules">按期望顺序排列的规则。</param>
    [RelayCommand]
    public async Task ReorderRulesAsync(IReadOnlyList<CategoryRule>? orderedRules)
    {
        if (orderedRules is null || orderedRules.Count == 0)
        {
            return;
        }

        var ids = orderedRules.Select(r => r.Id).ToList();
        await _rules.UpdatePrioritiesAsync(ids);
        await LoadAsync();

        StatusText = "优先级已更新。";
    }

    /// <summary>对全库执行批量重新匹配。</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRules))]
    public async Task ApplyRulesAsync()
    {
        if (_isApplying)
        {
            return;
        }

        _isApplying = true;
        IsBusy = true;
        ApplyRulesCommand.NotifyCanExecuteChanged();

        var processed = 0;
        var matched = 0;
        var dangerous = 0;

        try
        {
            var enabledRules = await _rules.GetEnabledAsync();

            if (enabledRules.Count == 0)
            {
                StatusText = "没有可参与的规则（规则或其分类未启用）。";
                return;
            }

            var skip = 0;

            while (true)
            {
                var page = await _mediaItems.QueryAsync(new MediaQuery
                {
                    Skip = skip,
                    Take = MatchPageSize
                });

                if (page.Count == 0)
                {
                    break;
                }

                var (report, results) = CategoryRuleEngine.MatchBatch(page, enabledRules);

                await _rules.ApplyMatchesAsync(results);

                processed += report.ProcessedCount;
                matched += report.MatchedCount;
                dangerous += report.DangerousRuleCount;

                if (dangerous > 0)
                {
                    // 出现超时规则即中止，避免用户等待一个跑不完的任务。
                    StatusText = $"已中止：检测到 {dangerous} 条规则存在灾难性回溯风险，请修正后重试。";
                    return;
                }

                if (report.WasCancelled || page.Count < MatchPageSize)
                {
                    break;
                }

                skip += page.Count;
            }

            StatusText = $"匹配完成：处理 {processed} 项，命中 {matched} 项。";
        }
        finally
        {
            _isApplying = false;
            IsBusy = false;
            ApplyRulesCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>是否可执行批量匹配。</summary>
    public bool CanApplyRules => !_isApplying;
}
