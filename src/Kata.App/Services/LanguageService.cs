using System.Globalization;

namespace Kata.App.Services;

public enum AppLanguage
{
    /// <summary>OS の表示言語に合わせる。</summary>
    System,
    Japanese,
    English,
}

public interface ILanguageService
{
    AppLanguage Selected { get; }

    /// <summary>選択を保存し、現在のスレッドにも当てる。ただし表示中の画面は作り直されない。</summary>
    void Apply(AppLanguage language);

    /// <summary>最初の画面を作る前に呼ぶ。</summary>
    void Initialize();
}

/// <summary>
/// 表示言語を決める。
///
/// WPF の文字列は生成時に一度読まれるだけなので途中で切り替えても開いている画面は変わらず、
/// 動的に組み立てたステータス文言だけが元の言語のまま残る。中途半端に混ざるより、
/// 次に開く画面から揃うほうが分かりやすい。
/// </summary>
public sealed class LanguageService : ILanguageService
{
    private readonly IAppSettingsStore _store;
    private AppSettings _settings = new();

    public LanguageService(IAppSettingsStore store) => _store = store;

    public AppLanguage Selected => _settings.Language;

    public void Initialize()
    {
        _settings = _store.Load();
        ApplyToThread(_settings.Language);
    }

    public void Apply(AppLanguage language)
    {
        // 別コードパス (Preferences の AI モデル保存など) が同時期にファイルへ書いた
        // 可能性があるので、書き出す直前にディスクを読み直す。キャッシュだけ更新して
        // 保存すると相手側の書き込みを踏み潰すことになる。
        _settings = _store.Load();
        _settings.Language = language;
        _store.Save(_settings);
        ApplyToThread(language);
    }

    private static void ApplyToThread(AppLanguage language)
    {
        var culture = Resolve(language);
        if (culture is null)
        {
            // OS 追従。既定のまま触らない
            CultureInfo.DefaultThreadCurrentUICulture = null;
            return;
        }

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>OS 追従なら null を返す。</summary>
    private static CultureInfo? Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Japanese => new CultureInfo("ja"),
        AppLanguage.English => new CultureInfo("en"),
        _ => null,
    };
}
