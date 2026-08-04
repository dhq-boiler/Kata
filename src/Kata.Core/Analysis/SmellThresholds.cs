namespace Kata.Core.Analysis;

/// <summary>
/// 全 universal detector が参照する閾値の集約点。将来の Preferences UI から
/// 上書き可能にする想定で 1 か所に集めてある。
/// </summary>
public static class SmellThresholds
{
    public const int LongFunctionLines = 30;
    public const int LongParameterListCount = 3;      // > this ⇒ smell
    public const int LargeClassMembers = 15;          // > this ⇒ smell
    public const int CommentsPerMethod = 3;           // >= this ⇒ smell (block or line trivia)
    public const int PrimitiveObsessionMinCount = 3;  // >= this parameters/fields all-primitive ⇒ smell
    public const int LazyElementMaxBodyLines = 3;
    public const int DataClumpsSize = 3;              // >= this consecutive params shared across methods
    public const int MessageChainDepth = 4;
    public const int DuplicatedCodeMinChars = 120;    // normalize後の body 長 (これ以下は無視)
}
