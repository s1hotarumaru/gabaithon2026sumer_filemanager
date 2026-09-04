using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections;
using System.IO;
using System.Text.Json;
using FileOrganizer.Shared.Models;
using FileOrganizer.UI.Mvvm;
using FileOrganizer.UI.Services;
using Microsoft.Win32;

namespace FileOrganizer.UI.ViewModels;

public sealed class RulesViewModel : ObservableObject
{
    private readonly IFrontendBackendGateway _gateway;
    private readonly Action<string> _showMessage;
    private RuleItemViewModel? _selectedRule;
    private bool _hasUnsavedChanges;

    public RulesViewModel(IFrontendBackendGateway gateway, Action<string> showMessage)
    {
        _gateway = gateway;
        _showMessage = showMessage;

        AddRuleCommand = new RelayCommand(AddRule);
        DeleteRuleCommand = new RelayCommand(DeleteSelected, () => SelectedRule is not null);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => SelectedRule is not null && Rules.IndexOf(SelectedRule) > 0);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => SelectedRule is not null && Rules.IndexOf(SelectedRule) < Rules.Count - 1);
        DuplicateCommand = new RelayCommand(DuplicateSelected, () => SelectedRule is not null);
        AddConditionCommand = new RelayCommand(() =>
        {
            SelectedRule?.Conditions.Add(new ConditionEditorViewModel(new RuleCondition { Type = "extension", Operator = "equals", Value = ".pdf" }));
            MarkChanged();
        }, () => SelectedRule is not null);
        RemoveConditionCommand = new RelayCommand(parameter =>
        {
            if (SelectedRule is not null && parameter is ConditionEditorViewModel condition)
            {
                SelectedRule.Conditions.Remove(condition);
                MarkChanged();
            }
        });
        AddActionCommand = new RelayCommand(() =>
        {
            SelectedRule?.Actions.Add(new ActionEditorViewModel(new RuleAction { Type = "move", Destination = @"C:\Users\demo\Documents" }));
            MarkChanged();
        }, () => SelectedRule is not null);
        RemoveActionCommand = new RelayCommand(parameter =>
        {
            if (SelectedRule is not null && parameter is ActionEditorViewModel action)
            {
                SelectedRule.Actions.Remove(action);
                MarkChanged();
            }
        });
        BrowseWatchFolderCommand = new RelayCommand(BrowseWatchFolder, () => SelectedRule is not null);
        BrowseActionDestinationCommand = new RelayCommand(parameter =>
        {
            if (parameter is not ActionEditorViewModel action) return;
            var dialog = new OpenFolderDialog { Title = "整理先フォルダーを選択", Multiselect = false };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
            action.Argument = dialog.FolderName;
        });
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => Rules.Count > 0);
    }

    public ObservableCollection<RuleItemViewModel> Rules { get; } = new();
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand DeleteRuleCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand AddConditionCommand { get; }
    public RelayCommand RemoveConditionCommand { get; }
    public RelayCommand AddActionCommand { get; }
    public RelayCommand RemoveActionCommand { get; }
    public RelayCommand BrowseWatchFolderCommand { get; }
    public RelayCommand BrowseActionDestinationCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    public RuleItemViewModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (SetProperty(ref _selectedRule, value))
                NotifyCommandStates();
        }
    }

    public bool HasRules => Rules.Count > 0;

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    public void Load(IEnumerable<RuleModel> rules)
    {
        Rules.Clear();
        foreach (var rule in rules)
        {
            var item = new RuleItemViewModel(rule);
            item.PropertyChanged += (_, _) => MarkChanged();
            Rules.Add(item);
        }

        SelectedRule = Rules.FirstOrDefault();
        HasUnsavedChanges = false;
        OnPropertyChanged(nameof(HasRules));
        NotifyCommandStates();
    }

    private void AddRule()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string watchFolder = Path.Combine(userProfile, "Downloads");
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var model = new RuleModel
        {
            Name = "新しい整理ルール",
            Enabled = true,
            WatchFolder = watchFolder,
            Conditions = new List<RuleCondition>
            {
                new() { Type = "extension", Operator = "equals", Value = ".pdf" }
            },
            Actions = new List<RuleAction>
            {
                new() { Type = "move", Destination = documents }
            }
        };
        var item = new RuleItemViewModel(model);
        item.PropertyChanged += (_, _) => MarkChanged();
        Rules.Add(item);
        SelectedRule = item;
        MarkChanged();
        OnPropertyChanged(nameof(HasRules));
    }

    private void DeleteSelected()
    {
        if (SelectedRule is null)
            return;

        int index = Rules.IndexOf(SelectedRule);
        Rules.Remove(SelectedRule);
        SelectedRule = Rules.Count == 0 ? null : Rules[Math.Clamp(index, 0, Rules.Count - 1)];
        MarkChanged();
        OnPropertyChanged(nameof(HasRules));
    }

    private void DuplicateSelected()
    {
        if (SelectedRule is null)
            return;

        var copy = SelectedRule.ToModel();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += "（コピー）";
        var item = new RuleItemViewModel(copy);
        item.PropertyChanged += (_, _) => MarkChanged();
        Rules.Insert(Rules.IndexOf(SelectedRule) + 1, item);
        SelectedRule = item;
        MarkChanged();
    }

    private void MoveSelected(int offset)
    {
        if (SelectedRule is null)
            return;

        int oldIndex = Rules.IndexOf(SelectedRule);
        int newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= Rules.Count)
            return;

        Rules.Move(oldIndex, newIndex);
        MarkChanged();
        NotifyCommandStates();
    }

    private void BrowseWatchFolder()
    {
        if (SelectedRule is null) return;

        var dialog = new OpenFolderDialog { Title = "監視するフォルダーを選択", Multiselect = false };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        SelectedRule.WatchFolder = dialog.FolderName;
    }

    private async Task SaveAsync()
    {
        try
        {
            await _gateway.SaveRulesAsync(Rules.Select(rule => rule.ToModel()).ToList());
            HasUnsavedChanges = false;
            _showMessage(_gateway.IsBackendConnected
                ? "整理ルールを保存しました。"
                : "ルールをUI確認用メモリへ保存しました（実ファイルは変更していません）。");
        }
        catch (Exception ex)
        {
            _showMessage($"整理ルールを保存できませんでした: {ex.Message}");
        }
    }

    private void MarkChanged()
    {
        HasUnsavedChanges = true;
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        DeleteRuleCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        AddConditionCommand.NotifyCanExecuteChanged();
        AddActionCommand.NotifyCanExecuteChanged();
        BrowseWatchFolderCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }
}

public sealed class RuleItemViewModel : ObservableObject
{
    private string _name;
    private bool _enabled;
    private string _watchFolder;

    public RuleItemViewModel(RuleModel model)
    {
        Id = model.Id;
        _name = model.Name;
        _enabled = model.Enabled;
        _watchFolder = model.WatchFolder;
        Conditions = new ObservableCollection<ConditionEditorViewModel>(model.Conditions.Select(condition => new ConditionEditorViewModel(condition)));
        Actions = new ObservableCollection<ActionEditorViewModel>(model.Actions.Select(action => new ActionEditorViewModel(action)));
        foreach (var condition in Conditions)
            condition.PropertyChanged += OnEditorPropertyChanged;
        foreach (var action in Actions)
            action.PropertyChanged += OnEditorPropertyChanged;
        Conditions.CollectionChanged += OnConditionsChanged;
        Actions.CollectionChanged += OnActionsChanged;
    }

    public string Id { get; }
    public ObservableCollection<ConditionEditorViewModel> Conditions { get; }
    public ObservableCollection<ActionEditorViewModel> Actions { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string WatchFolder
    {
        get => _watchFolder;
        set => SetProperty(ref _watchFolder, value);
    }

    public string Summary => $"条件 {Conditions.Count}件  ・  アクション {Actions.Count}件";

    public RuleModel ToModel() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Enabled = Enabled,
        WatchFolder = WatchFolder.Trim(),
        Conditions = Conditions.Select(condition => condition.ToModel()).ToList(),
        Actions = Actions.Select(action => action.ToModel()).ToList()
    };

    private void OnConditionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ConditionEditorViewModel item in e.OldItems)
                item.PropertyChanged -= OnEditorPropertyChanged;
        }
        if (e.NewItems is not null)
        {
            foreach (ConditionEditorViewModel item in e.NewItems)
                item.PropertyChanged += OnEditorPropertyChanged;
        }
        OnPropertyChanged(nameof(Summary));
    }

    private void OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ActionEditorViewModel item in e.OldItems)
                item.PropertyChanged -= OnEditorPropertyChanged;
        }
        if (e.NewItems is not null)
        {
            foreach (ActionEditorViewModel item in e.NewItems)
                item.PropertyChanged += OnEditorPropertyChanged;
        }
        OnPropertyChanged(nameof(Summary));
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Summary));
}

public sealed class ConditionEditorViewModel : ObservableObject
{
    private string _type;
    private string _operator;
    private string _value;

    public ConditionEditorViewModel(RuleCondition model)
    {
        _type = model.Type;
        _operator = model.Operator;
        _value = FormatValueForEditor(model.Value);
    }

    public static IReadOnlyList<SelectionOption> TypeOptions { get; } = new[]
    {
        new SelectionOption("extension", "拡張子"),
        new SelectionOption("filename", "ファイル名"),
        new SelectionOption("size_mb", "サイズ (MB)"),
        new SelectionOption("days_old", "経過日数"),
        new SelectionOption("ocr_contains", "本文テキスト"),
        new SelectionOption("ai_category", "AIカテゴリ")
    };

    public static IReadOnlyList<SelectionOption> OperatorOptions { get; } = new[]
    {
        new SelectionOption("equals", "等しい"),
        new SelectionOption("contains", "含む"),
        new SelectionOption("regex", "正規表現"),
        new SelectionOption("greater_than", "より大きい"),
        new SelectionOption("less_than", "より小さい"),
        new SelectionOption("in", "いずれか")
    };

    public string Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
                OnPropertyChanged(nameof(ValueExample));
        }
    }

    public string Operator
    {
        get => _operator;
        set
        {
            if (SetProperty(ref _operator, value))
                OnPropertyChanged(nameof(ValueExample));
        }
    }

    public string Value { get => _value; set => SetProperty(ref _value, value); }

    /// <summary>種類・演算子に応じた入力例（プレースホルダー用）。</summary>
    public string ValueExample
    {
        get
        {
            string baseExample = Type switch
            {
                "extension" => "例: .pdf",
                "filename" => "例: 見積書",
                "size_mb" => "例: 10",
                "days_old" => "例: 30",
                "ocr_contains" => "例: 領収書",
                // AIカテゴリの値はpy_service（DOCUMENT_TYPE_LABELS）が返す日本語ラベルそのもの。
                // 複数種別を対象にしたい場合は演算子を「いずれか」にしてカンマ区切りで指定する
                // （「含む」1つのカテゴリ名に対してのみ意味を持つ。カンマ区切り文字列との組み合わせは
                //   常に不一致になり、リネーム/移動が一切実行されない不具合の原因になる）。
                "ai_category" => "例: 領収書、請求書、議事録、契約書、その他",
                _ => "例: 値"
            };
            return Operator == "in" ? baseExample + "（カンマ区切りで複数指定可: .jpg, .png）" : baseExample;
        }
    }

    public RuleCondition ToModel()
    {
        string trimmed = Value.Trim();
        object value = Operator == "in"
            ? trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : trimmed;

        return new RuleCondition { Type = Type, Operator = Operator, Value = value };
    }

    private static string FormatValueForEditor(object? value)
    {
        if (value is null) return string.Empty;
        if (value is string text) return text;
        if (value is JsonElement { ValueKind: JsonValueKind.Array } jsonArray)
        {
            return string.Join(", ", jsonArray.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText()));
        }
        if (value is IEnumerable values)
        {
            return string.Join(", ", values.Cast<object?>().Select(item => item?.ToString() ?? string.Empty));
        }
        return value.ToString() ?? string.Empty;
    }
}

public sealed class ActionEditorViewModel : ObservableObject
{
    private string _type;
    private string _argument;

    public ActionEditorViewModel(RuleAction model)
    {
        _type = model.Type;
        _argument = model.Type == "rename" ? model.Pattern ?? string.Empty : model.Destination ?? string.Empty;
    }

    public static IReadOnlyList<SelectionOption> TypeOptions { get; } = new[]
    {
        new SelectionOption("move", "移動する"),
        new SelectionOption("copy", "コピーする"),
        new SelectionOption("rename", "名前を変更する"),
        new SelectionOption("recycle", "ゴミ箱へ送る")
    };

    public string Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(ArgumentLabel));
                OnPropertyChanged(nameof(HasArgument));
                OnPropertyChanged(nameof(ArgumentExample));
                OnPropertyChanged(nameof(IsFolderArgument));
            }
        }
    }

    public string Argument { get => _argument; set => SetProperty(ref _argument, value); }
    public string ArgumentLabel => Type == "rename" ? "命名パターン" : Type == "recycle" ? "追加設定なし" : "整理先フォルダ";
    public bool HasArgument => Type != "recycle";

    /// <summary>「参照...」ボタンを出すべきか（移動・コピー先はフォルダ選択、名前変更は文字列パターンのため対象外）。</summary>
    public bool IsFolderArgument => Type is "move" or "copy";

    /// <summary>アクション種別に応じた入力例。命名パターンは使えるトークンと変換結果の例を示す。</summary>
    public string ArgumentExample => Type switch
    {
        "rename" => "例: {filename}_{category}{ext} → 見積書_finance.pdf　※使えるトークン: {filename}(元のファイル名) {ext}(拡張子) {category}(AI分類結果)",
        "recycle" => string.Empty,
        _ => @"例: C:\Users\name\Documents\請求書"
    };

    public RuleAction ToModel() => new()
    {
        Type = Type,
        Pattern = Type == "rename" ? Argument.Trim() : null,
        Destination = Type is "move" or "copy" ? Argument.Trim() : null
    };
}

public sealed record SelectionOption(string Value, string Label);
