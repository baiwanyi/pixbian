/**
 * 分类规则管理视图模型（M5）。
 * 职责：管理分类与规则的增删改查、启停，以及批量重新匹配。
 *      优先级调整已实现（ReorderRulesAsync），但当前无界面入口，属未接线能力。
 * 复用约定：批量匹配走 CategoryRuleEngine，结果经仓储批量写回；
 *          所有集合修改一律切回 UI 线程执行（仓储内部使用 ConfigureAwait(false)）。
 * 关键约束：新增或修改规则前必须先校验正则——语法非法或存在性能风险的规则不得入库，
 *          否则一条会回溯的正则就会拖死索引线程；
 *          批量重匹配分批处理（分页读取 + 逐页写回），全库一次性匹配会让界面长时间无响应；
 *          当前**无用户取消入口**，超时兜底只来自整批 30 秒总时限（CategoryRuleEngine）。
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

/// <summary>分类规则管理视图模型。</summary>
public sealed partial class CategoryViewModel : ObservableObject
{
    private const int MatchPageSize = 1000;

    private readonly ICategoryRepository _categories;
    private readonly ICategoryRuleRepository _rules;
    private readonly IMediaItemRepository _mediaItems;
    private readonly DispatcherQueue _dispatcherQueue;

    private bool _isApplying;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _isBusy;

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

    /// <summary>规则集合。</summary>
    public ObservableCollection<CategoryRule> Rules { get; } = [];

    /// <summary>是否已有可归类的分类。</summary>
    public bool HasCategories => Categories.Count > 0;

    /// <summary>新增规则的正则是否合法。</summary>
    public bool IsNewRuleValid =>
        CategoryRuleHelper.ValidatePattern(NewRulePattern).IsValid && NewRuleCategoryId > 0;

    /// <summary>新增规则的校验提示。</summary>
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

    /// <summary>通知界面校验文本需要刷新。</summary>
    public void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(IsNewRuleValid));
        OnPropertyChanged(nameof(NewRuleValidationText));
    }

    /// <summary>加载分类与规则。</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var categories = await _categories.GetAllAsync();
        var rules = await _rules.GetAllAsync();

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
                Rules.Add(rule);
            }

            OnPropertyChanged(nameof(HasCategories));
            StatusText = $"共 {Categories.Count} 个分类、{Rules.Count} 条规则";
        });
    }

    /// <summary>新增分类。</summary>
    /// <param name="name">分类名称。</param>
    [RelayCommand]
    public async Task AddCategoryAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var created = await _categories.AddAsync(name.Trim());

        await LoadAsync();
        NewRuleCategoryId = created.Id;
        OnPropertyChanged(nameof(NewRuleCategoryId));
        NotifyValidationChanged();
    }

    /// <summary>新增规则；正则校验不通过时拒绝保存。</summary>
    [RelayCommand(CanExecute = nameof(IsNewRuleValid))]
    public async Task AddRuleAsync()
    {
        if (!IsNewRuleValid)
        {
            StatusText = "正则不可用，请先修正。";
            return;
        }

        var rule = new CategoryRule
        {
            Name = string.IsNullOrWhiteSpace(NewRuleName) ? NewRulePattern : NewRuleName.Trim(),
            Pattern = NewRulePattern.Trim(),
            CategoryId = NewRuleCategoryId,
            Target = NewRuleTarget,
            IsCaseSensitive = NewRuleCaseSensitive,
            IsEnabled = true,
            Priority = 0
        };

        await _rules.AddAsync(rule);

        NewRuleName = string.Empty;
        NewRulePattern = string.Empty;
        NotifyValidationChanged();

        await LoadAsync();
        StatusText = "规则已添加。";
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
                StatusText = "没有已启用的规则。";
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
