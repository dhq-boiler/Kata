using System.Windows;
using Kata.App.Diagnostics;
using Kata.App.Graph;
using Kata.App.PluginApi;
using Kata.App.Services;

namespace Kata.App;

public partial class App : Application
{
    public static string? RequestedSolutionPath { get; private set; }

    // DI コンテナは入れていないので、App がサービスを保持する。取り出しは Services 経由。
    // Preferences ダイアログや MainWindow から使う
    public static IAppSettingsStore SettingsStore { get; } = new JsonAppSettingsStore();
    public static ILanguageService LanguageService { get; } = new LanguageService(SettingsStore);
    public static KataMcpBridge McpBridge { get; } = new();

    // Community 側で保持するライセンスキーの読み書き。実際の判定 (Pro か否か) は
    // Kata.App.Pro.dll (存在すれば) が受け取って行う。
    public static LicenseStorage LicenseStore { get; } = new();

    // P3 (plugin) 方式の Pro 機能ゲート。Kata.App.Pro.dll が存在すれば本実装、
    // 無ければ NoOpProFeatures (常に Community)。フォールバックは ProLoader 内で完結。
    public static IProFeatures ProFeatures { get; } = ProLoader.Load(LicenseStore);

    // AI 相談の月次カウンタ。Claude / Codex は同一 budget を共有する。
    public static AiUsageStore AiUsage { get; } = new();

    // Community 版では QuotaGatedAiInvoker で月 10 回に絞る。Pro 版では素通し。
    public static IAiInvoker ClaudeCli { get; } =
        new QuotaGatedAiInvoker(new ClaudeCliClient(SettingsStore), ProFeatures, AiUsage);
    public static IAiInvoker CodexCli { get; } =
        new QuotaGatedAiInvoker(new CodexCliClient(SettingsStore), ProFeatures, AiUsage);

    protected override void OnStartup(StartupEventArgs e)
    {
        // MainWindow が構築される前に UI カルチャを固定する。XAML の {x:Static loc:Strings.*}
        // は生成時の CurrentUICulture で解決されるので、base.OnStartup より前にやる必要がある
        LanguageService.Initialize();

        // 診断モード (%TEMP%\kata-diag.log への逐次ログ) は起動時に settings から反映。
        // 以降 Preferences から on/off できる (PreferencesViewModel.Ok が DiagLog.Enabled を更新)。
        DiagLog.Enabled = SettingsStore.Load().DiagnosticsEnabled;

        // ProLoader は static init (App クラス触った瞬間) で走る = DiagLog がまだ無効。
        // ログは内部 buffer に貯めてあるので、DiagLog 有効化直後に flush する。
        // これで「Pro DLL があるのにロードされない」ケースの support 診断が可能になる。
        ProLoader.FlushBufferedLogs();

        base.OnStartup(e);
        if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
        {
            RequestedSolutionPath = e.Args[0];
        }

        // Must run before any NodifyEditor is constructed — EditorGestures.Mappings
        // is the source of default gestures for every editor instance.
        TrackpadPanZoom.ConfigureGestures();

        // Start the UI hitch probe as early as possible so we see every stall,
        // including the initial sln load.
        UiHitchMonitor.StartFor(Dispatcher);
    }
}
