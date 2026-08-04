namespace Kata.Core.Analysis;

// Fowler "Refactoring" 2nd ed. — 24 code smells.
public enum SmellCategory
{
    MysteriousName,                          // 不可思議な名前
    DuplicatedCode,                          // 重複したコード
    LongFunction,                            // 長い関数
    LongParameterList,                       // 長いパラメータリスト
    GlobalData,                              // グローバルなデータ
    MutableData,                             // 変更可能なデータ
    DivergentChange,                         // 変更の偏り
    ShotgunSurgery,                          // 変更の分散
    FeatureEnvy,                             // 特性の横恋慕
    DataClumps,                              // データの群れ
    PrimitiveObsession,                      // 基本データ型への執着
    RepeatedSwitches,                        // 重複したスイッチ文
    Loops,                                   // ループ
    LazyElement,                             // 怠け者の要素
    SpeculativeGenerality,                   // 疑わしき一般化
    TemporaryField,                          // 一時的属性
    MessageChains,                           // メッセージの連鎖
    MiddleMan,                               // 仲介人
    InsiderTrading,                          // インサイダー取引
    LargeClass,                              // 巨大なクラス
    AlternativeClassesWithDifferentInterfaces, // クラスのインターフェース不一致
    DataClass,                               // データクラス
    RefusedBequest,                          // 相続拒否
    Comments,                                // コメント
}
