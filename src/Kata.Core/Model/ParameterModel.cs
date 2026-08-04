namespace Kata.Core.Model;

/// <summary>
/// メソッド / コンストラクタの 1 パラメータ。TypeDisplay は adapter が表出させる生テキスト
/// (C#: "System.Int32", "IReadOnlyList&lt;string&gt;" / C++/CLI: "int", "AudioBuffer^") で、
/// smell 検出は文字列ヒューリスティックで判定する (primitive 判定など)。
/// </summary>
public sealed record ParameterModel(string Name, string TypeDisplay);
