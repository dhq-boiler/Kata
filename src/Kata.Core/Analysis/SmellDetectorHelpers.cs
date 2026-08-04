using Kata.Core.Model;

namespace Kata.Core.Analysis;

/// <summary>
/// universal detector が共有するヘルパー。名前判定、primitive 型判定、
/// メソッド抽出などの小道具。
/// </summary>
public static class SmellDetectorHelpers
{
    // "Primitive-like" — Fowler primitive obsession 判定用の広いネット。
    // Roslyn 側の SpecialType 相当 + Cpp/CLI の raw type text (int / float / bool / char /
    // System::String^ / System.String / const wchar_t* など) をどちらも拾えるように
    // 文字列ヒューリスティックで判定する。
    private static readonly HashSet<string> PrimitiveDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bool", "boolean", "byte", "sbyte",
        "short", "ushort", "int", "uint", "long", "ulong",
        "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64",
        "float", "double", "decimal", "single",
        "char", "wchar_t", "string",
        "System.Boolean", "System.Byte", "System.SByte",
        "System.Int16", "System.UInt16", "System.Int32", "System.UInt32",
        "System.Int64", "System.UInt64",
        "System.Single", "System.Double", "System.Decimal",
        "System.Char", "System.String",
        // C++/CLI
        "System::String^", "System::Object^",
    };

    /// <summary>
    /// 表示テキストが primitive-ish かの緩い判定。
    /// "const int&amp;" / "IReadOnlyList&lt;int&gt;" のような装飾付きは基本 false に倒す
    /// (対応しない)。素の型名 + 少しの装飾なら拾う。
    /// </summary>
    public static bool IsPrimitiveDisplay(string typeDisplay)
    {
        if (string.IsNullOrWhiteSpace(typeDisplay)) return false;
        var trimmed = typeDisplay.Trim();
        // C++/CLI 参照修飾 '^' を取ってから引く
        if (trimmed.EndsWith("^", StringComparison.Ordinal))
            trimmed = trimmed[..^1].Trim();
        return PrimitiveDisplayNames.Contains(trimmed);
    }

    // Mysterious-name 検出用ヘルパー
    private static readonly HashSet<string> AllowedShortNames = new(StringComparer.Ordinal)
    {
        "i", "j", "k", "n", "m", "x", "y", "z", "t", "e",
        "T", "U", "V", "TResult", "TSource", "TKey", "TValue",
    };

    private static readonly HashSet<string> PlaceholderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "foo", "bar", "baz", "qux", "tmp", "temp", "stuff", "thing", "misc",
        "data1", "obj1", "test1", "asdf", "todo",
    };

    public static bool IsMysteriousName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (AllowedShortNames.Contains(name)) return false;
        if (PlaceholderNames.Contains(name)) return true;
        if (name.Length <= 2 && !AllowedShortNames.Contains(name)) return true;
        return false;
    }

    /// <summary>
    /// 手書きメソッド (constructor / method) を返す。accessor 相当は
    /// adapter 側が既に MemberModel から落としているので、Kind でフィルタするだけで足りる。
    /// </summary>
    public static IEnumerable<MemberModel> Methods(TypeModel type)
    {
        foreach (var m in type.Members)
        {
            if (m.Kind is MemberKind.Method or MemberKind.Constructor)
                yield return m;
        }
    }

    /// <summary>
    /// state を持つメンバー (field / property / event) を返す。
    /// </summary>
    public static IEnumerable<MemberModel> StateMembers(TypeModel type)
    {
        foreach (var m in type.Members)
        {
            if (m.Kind is MemberKind.Field or MemberKind.Property or MemberKind.Event)
                yield return m;
        }
    }
}
