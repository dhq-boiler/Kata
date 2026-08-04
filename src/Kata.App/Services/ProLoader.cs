using System.IO;
using System.Reflection;
using Kata.App.Diagnostics;
using Kata.App.PluginApi;

namespace Kata.App.Services;

// Kata.App.Pro.dll を実行時 (Assembly.LoadFrom) で探して、見つかれば IProFeatures
// 実装を差し替える。見つからない or ロード失敗なら NoOpProFeatures (常に Community)
// にフォールバックする。
//
// これが P3 (plugin) 方式の要。Community 版バイナリには Pro コードは一切含まれず、
// 契約者に配布する Pro installer だけが Kata.App.Pro.dll を同梱する。
//
// 署名検証や強い loader gate は入れない (Fable レビュー: クラック鼬ごっこ回避)。
// ライセンス検証は Pro 側 (ILicenseValidator) が担当し、失敗すれば Pro 実装が
// 自分で LicenseStatus.Invalid を返して自動的に Community 扱いになる。
//
// 診断ログ (buffered): App の static init 段階で走るため DiagLog.Enabled が
// まだ false。 buffer に貯めて、OnStartup で有効化された後 FlushBufferedLogs()
// で吐き出す。
public static class ProLoader
{
    private const string PluginFileName = "Kata.App.Pro.dll";
    private const string ImplementationTypeName = "Kata.App.Pro.ProFeaturesImpl";
    private const string PluginApiVersionTypeName = "Kata.App.PluginApi.PluginApiVersion";

    private static readonly List<string> _bufferedLogs = new();
    private static readonly object _bufferSync = new();

    // OnStartup 完了後に呼んで、static init 中に貯まった [pro] ログを DiagLog に流す。
    public static void FlushBufferedLogs()
    {
        lock (_bufferSync)
        {
            foreach (var line in _bufferedLogs) DiagLog.Line(line);
            _bufferedLogs.Clear();
        }
    }

    private static void Log(string line)
    {
        lock (_bufferSync) _bufferedLogs.Add(line);
    }

    public static IProFeatures Load(LicenseStorage licenseStorage)
    {
        var pluginPath = ResolvePluginPath();
        if (pluginPath is null || !File.Exists(pluginPath))
        {
            Log("[pro] Kata.App.Pro.dll not present — running as Community.");
            return new NoOpProFeatures();
        }

        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(pluginPath);
        }
        catch (BadImageFormatException ex)
        {
            // 破損 DLL or アーキテクチャ不一致。契約者にとって「Pro DLL があるのに動かない」
            // 状態なので DisplayMessage で通知する。
            var msg = $"Kata.App.Pro.dll is present but could not be loaded (BadImageFormat: {ex.Message}). Reinstall the Pro edition.";
            Log($"[pro] {msg}");
            return new NoOpProFeatures(msg);
        }
        catch (FileLoadException ex)
        {
            var msg = $"Kata.App.Pro.dll is present but failed to load ({ex.Message}). Check permissions / antivirus.";
            Log($"[pro] {msg}");
            return new NoOpProFeatures(msg);
        }
        catch (Exception ex)
        {
            var msg = $"Kata.App.Pro.dll load threw {ex.GetType().Name}: {ex.Message}. Reinstall or contact support.";
            Log($"[pro] {msg}");
            return new NoOpProFeatures(msg);
        }

        // Major バージョン互換チェック — 破壊的変更が入った旧 Pro DLL を silent downgrade
        // させない (Fable M2: 契約者が無言で Pro を失うのを防ぐ)。
        if (!CheckApiVersionCompatibility(asm, out var versionWarning))
        {
            Log($"[pro] {versionWarning}");
            return new NoOpProFeatures(versionWarning);
        }

        // 実装 type の探索
        Type? implType;
        try
        {
            implType = asm.GetType(ImplementationTypeName)
                ?? asm.GetTypes().FirstOrDefault(t => typeof(IProFeatures).IsAssignableFrom(t) && !t.IsAbstract);
        }
        catch (ReflectionTypeLoadException ex)
        {
            var missing = string.Join(", ", ex.LoaderExceptions
                .Where(e => e is not null)
                .Take(3)
                .Select(e => e!.Message));
            var msg = $"Kata.App.Pro.dll has type-load errors (possibly PluginApi version mismatch): {missing}";
            Log($"[pro] {msg}");
            return new NoOpProFeatures(msg);
        }

        if (implType is null)
        {
            var msg = $"Kata.App.Pro.dll loaded but no IProFeatures implementation found. Reinstall the Pro edition.";
            Log($"[pro] {msg}");
            return new NoOpProFeatures(msg);
        }

        // 実装コンストラクタは以下を順に試す (拡張しやすい単純な優先順):
        //   1) (LicenseStorage) — Community 側の storage を渡す (推奨)
        //   2) (string?)         — 現在の license key を直接渡す
        //   3) ()                — 引数なし
        object? instance;
        try
        {
            instance =
                TryConstruct(implType, new object[] { licenseStorage })
                ?? TryConstruct(implType, new object?[] { licenseStorage.LoadKey() })
                ?? TryConstruct(implType, Array.Empty<object>());
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            var inner = ex.InnerException;
            var msg = $"Kata.App.Pro.dll instantiation threw {inner.GetType().Name}: {inner.Message}. Reinstall or contact support.";
            Log($"[pro] {msg}");
            return new NoOpProFeatures(msg);
        }
        catch (MissingMethodException ex)
        {
            var msg = $"Kata.App.Pro.dll constructor signature mismatch (PluginApi version mismatch?): {ex.Message}";
            Log($"[pro] {msg}");
            return new NoOpProFeatures(msg);
        }

        if (instance is IProFeatures features)
        {
            Log($"[pro] loaded {implType.FullName} (IsPro={features.IsPro}, tier={features.License.Tier}).");
            return features;
        }

        var noMatchMsg = "Kata.App.Pro.dll returned an incompatible IProFeatures instance. Reinstall the Pro edition.";
        Log($"[pro] {noMatchMsg}");
        return new NoOpProFeatures(noMatchMsg);
    }

    private static bool CheckApiVersionCompatibility(Assembly proAssembly, out string warning)
    {
        // Pro DLL 側で PluginApiVersion を参照するとその Assembly の Type が同じ
        // Kata.App.PluginApi assembly を指すので、GetType 経由でも Reflection 経由でも
        // Community 側 (現在動いてる) の PluginApiVersion を触ることになる。
        // ここでチェックしたいのは「Pro DLL が想定してる PluginApi Major」なので、
        // Pro DLL 内に埋め込まれた referenced-assembly の version を見る。
        try
        {
            var refs = proAssembly.GetReferencedAssemblies();
            var apiRef = refs.FirstOrDefault(r =>
                string.Equals(r.Name, "Kata.App.PluginApi", StringComparison.OrdinalIgnoreCase));
            if (apiRef?.Version is null)
            {
                // 参照が見えない or 版情報なし — 通しておく (壊れてたら次の Type 解決で捕まる)
                warning = string.Empty;
                return true;
            }

            var proExpectsMajor = apiRef.Version.Major;
            var current = PluginApiVersion.Major;
            if (proExpectsMajor != current)
            {
                warning =
                    $"Kata.App.Pro.dll targets PluginApi v{apiRef.Version} but Community host provides v{PluginApiVersion.Display}. " +
                    $"Update the Pro edition to match this Kata release.";
                return false;
            }

            if (apiRef.Version.Minor > PluginApiVersion.Minor)
            {
                // Pro が新しい Minor を期待している = Community が古い。動くかもだが警告
                Log($"[pro] Kata.App.Pro.dll targets PluginApi v{apiRef.Version} (newer minor than host v{PluginApiVersion.Display}). Proceeding.");
            }

            warning = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            // 参照解析失敗は fatal 扱いしない。Type 解決段階で明示的に落ちるならそっちで報告される。
            Log($"[pro] PluginApi version check failed to inspect assembly refs ({ex.GetType().Name}: {ex.Message}). Skipping check.");
            warning = string.Empty;
            return true;
        }
    }

    private static string? ResolvePluginPath()
    {
        // Kata.App.exe と同一ディレクトリを最優先で探す。dev では bin\Debug 配下、
        // 配布版 (Pro installer) では installer が配置したパスに置かれる想定。
        var baseDir = AppContext.BaseDirectory;
        var probe = Path.Combine(baseDir, PluginFileName);
        return File.Exists(probe) ? probe : null;
    }

    private static object? TryConstruct(Type implType, object?[] args)
    {
        var argTypes = args.Select(a => a?.GetType() ?? typeof(object)).ToArray();
        var ctor = implType.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: argTypes,
            modifiers: null);
        if (ctor is null) return null;
        return ctor.Invoke(args);
        // 例外は上位で分類 catch する (TargetInvocationException 等)
    }
}
