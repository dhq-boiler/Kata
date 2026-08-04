using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kata.App.Diagnostics;
using Kata.App.Localization;
using Kata.App.PluginApi;
using Kata.App.Services;

namespace Kata.App.ViewModels;

/// <summary>
/// 環境設定ダイアログ。左にカテゴリ、右に選択したカテゴリの中身、下に OK/Cancel。
///
/// カテゴリを増やすときは <see cref="PreferencesCategory"/> を
/// <see cref="Categories"/> に足し、対応する DataTemplate を
/// PreferencesWindow.xaml の Style.Triggers に 1 個追加するだけ。
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly IAppSettingsStore _settings;
    private readonly ILanguageService _languageService;
    private readonly LicenseStorage _licenseStorage;
    private readonly IProFeatures _proFeatures;
    private readonly string _initialLicenseKey;

    public event EventHandler? Committed;
    public event EventHandler? Cancelled;

    public ObservableCollection<PreferencesCategory> Categories { get; } = new();

    [ObservableProperty] private PreferencesCategory? _selectedCategory;

    public string Title => Strings.Preferences_Title;
    public string LanguageLabel => Strings.Preferences_Language_Label;
    public string LanguageHint => Strings.Preferences_Language_Hint;
    public string ClaudeModelLabel => Strings.Preferences_Ai_ClaudeModel_Label;
    public string CodexModelLabel => Strings.Preferences_Ai_CodexModel_Label;
    public string AiModelHint => Strings.Preferences_Ai_ModelHint;
    public string DiagnosticsEnabledLabel => Strings.Preferences_Diagnostics_Enabled_Label;
    public string DiagnosticsHint => Strings.Preferences_Diagnostics_Hint;
    public string DiagnosticsLogPath => DiagLog.FilePath;
    public string ProStatusLabel => Strings.Preferences_Pro_StatusLabel;
    public string ProLicenseKeyLabel => Strings.Preferences_Pro_LicenseKeyLabel;
    public string ProHint => Strings.Preferences_Pro_Hint;
    public string ProRestartHint => Strings.Preferences_Pro_RestartHint;
    public string ProEmailLabel => Strings.Preferences_Pro_EmailLabel;
    public string ProExpiresLabel => Strings.Preferences_Pro_ExpiresLabel;
    public string OkLabel => Strings.Common_Ok;
    public string CancelLabel => Strings.Common_Cancel;

    // 現在の Pro 状態表示 (起動時に ProLoader が確定させたもの)。ライセンスキーを
    // 変更しても再起動するまで反映されないので、これは "起動時スナップショット" として扱う。
    public string ProStatusText => _proFeatures.License.Status switch
    {
        LicenseStatus.Active   => string.Format(Strings.Preferences_Pro_Status_Active_Format, _proFeatures.License.Tier),
        LicenseStatus.Invalid  => Strings.Preferences_Pro_Status_Invalid,
        LicenseStatus.Expired  => Strings.Preferences_Pro_Status_Expired,
        _                      => Strings.Preferences_Pro_Status_Community,
    };
    public string? ProEmailText => _proFeatures.License.Email;
    public bool ProHasEmail => !string.IsNullOrWhiteSpace(_proFeatures.License.Email);
    public string ProExpiresText => _proFeatures.License.ExpiresAtUtc is { } exp
        ? exp.ToLocalTime().ToString("yyyy-MM-dd")
        : Strings.Preferences_Pro_ExpiresPerpetual;
    public bool ProShowExpires => _proFeatures.License.IsPro;
    public string? ProDiagnosticMessage => _proFeatures.License.DisplayMessage;
    public bool ProHasDiagnostic => !string.IsNullOrWhiteSpace(_proFeatures.License.DisplayMessage);
    public bool ProShowRestartHint => !string.Equals(
        (_initialLicenseKey ?? string.Empty).Trim(),
        (LicenseKey ?? string.Empty).Trim(),
        StringComparison.Ordinal);

    [ObservableProperty] private string _licenseKey = string.Empty;

    partial void OnLicenseKeyChanged(string value) => OnPropertyChanged(nameof(ProShowRestartHint));

    // enum を直接 ComboBox に流すと "System" 表記になって読みにくいので、
    // 表示ラベル付きの record で持つ。Ok で Value を AppSettings に書き戻す
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = new[]
    {
        new LanguageOption(AppLanguage.System, Strings.Preferences_Language_System),
        // 各言語の呼称は自言語で書く。切替中の言語に関わらず同じ表記なので
        // Preferences_Language_System と違って resx を分けない
        new LanguageOption(AppLanguage.Japanese, "日本語"),
        new LanguageOption(AppLanguage.English, "English"),
    };

    // ComboBox IsEditable="True" + IsTextSearchEnabled="True" の候補として並べる。
    // ユーザーは候補外の任意の識別子も入力できる。
    public IReadOnlyList<string> ClaudeModelSuggestions { get; } = KnownAiModels.Claude;
    public IReadOnlyList<string> CodexModelSuggestions { get; } = KnownAiModels.Codex;

    [ObservableProperty] private LanguageOption? _selectedLanguageOption;
    [ObservableProperty] private string _claudeModel = string.Empty;
    [ObservableProperty] private string _codexModel = string.Empty;
    [ObservableProperty] private bool _diagnosticsEnabled;

    public PreferencesViewModel(
        IAppSettingsStore settings,
        ILanguageService languageService,
        LicenseStorage licenseStorage,
        IProFeatures proFeatures)
    {
        _settings = settings;
        _languageService = languageService;
        _licenseStorage = licenseStorage;
        _proFeatures = proFeatures;

        var current = settings.Load();
        _selectedLanguageOption = LanguageOptions.FirstOrDefault(o => o.Value == current.Language)
            ?? LanguageOptions[0];
        _claudeModel = current.ClaudeModel ?? string.Empty;
        _codexModel = current.CodexModel ?? string.Empty;
        _diagnosticsEnabled = current.DiagnosticsEnabled;
        _initialLicenseKey = _licenseStorage.LoadKey() ?? string.Empty;
        _licenseKey = _initialLicenseKey;

        Categories.Add(new PreferencesCategory(Strings.Preferences_Category_General, "general"));
        Categories.Add(new PreferencesCategory(Strings.Preferences_Category_Ai, "ai"));
        Categories.Add(new PreferencesCategory(Strings.Preferences_Category_Diagnostics, "diagnostics"));
        Categories.Add(new PreferencesCategory(Strings.Preferences_Category_Pro, "pro"));
        _selectedCategory = Categories[0];
    }

    [RelayCommand]
    private void Ok()
    {
        // 言語は LanguageService 経由 (ApplyToThread までやる)。ここで先に書いておくと
        // 直後の Load() で最新化された settings を取れる。
        if (SelectedLanguageOption is not null)
        {
            _languageService.Apply(SelectedLanguageOption.Value);
        }

        var current = _settings.Load();
        current.ClaudeModel = string.IsNullOrWhiteSpace(ClaudeModel) ? null : ClaudeModel.Trim();
        current.CodexModel = string.IsNullOrWhiteSpace(CodexModel) ? null : CodexModel.Trim();
        current.DiagnosticsEnabled = DiagnosticsEnabled;
        _settings.Save(current);

        // hot-path (ApplyAndReloadAsync / ApplyDiffOverlay 等) が読む volatile bool を即反映。
        DiagLog.Enabled = current.DiagnosticsEnabled;

        // ライセンスキーは license.json (別ファイル) に保存。反映は再起動時 (ProLoader 経由)。
        var newKey = string.IsNullOrWhiteSpace(LicenseKey) ? null : LicenseKey.Trim();
        if (!string.Equals(newKey ?? string.Empty, _initialLicenseKey, StringComparison.Ordinal))
        {
            _licenseStorage.SaveKey(newKey);
        }

        Committed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 環境設定ダイアログの左に並ぶカテゴリ。<see cref="Key"/> は XAML の
/// DataTemplate が判定に使う識別子で、表示名 <see cref="DisplayName"/> と分離してある。
/// </summary>
public sealed record PreferencesCategory(string DisplayName, string Key);

/// <summary>ComboBox に流すための言語選択肢。</summary>
public sealed record LanguageOption(AppLanguage Value, string Display);
