namespace Kata.Cpp.Semantics;

public sealed class CppMemberSymbolInfo
{
    public static CppMemberSymbolInfo NotFound { get; } =
        new(null, Array.Empty<CppMemberSymbol>(), CppCandidateReason.NotFound);

    public CppMemberSymbol? Symbol { get; }
    public IReadOnlyList<CppMemberSymbol> CandidateSymbols { get; }
    public CppCandidateReason CandidateReason { get; }

    internal CppMemberSymbolInfo(
        CppMemberSymbol? symbol,
        IReadOnlyList<CppMemberSymbol> candidateSymbols,
        CppCandidateReason candidateReason)
    {
        Symbol = symbol;
        CandidateSymbols = candidateSymbols;
        CandidateReason = candidateReason;
    }

    public static CppMemberSymbolInfo Resolved(CppMemberSymbol symbol)
        => new(symbol, Array.Empty<CppMemberSymbol>(), CppCandidateReason.None);

    public static CppMemberSymbolInfo Ambiguous(IReadOnlyList<CppMemberSymbol> candidates)
        => new(null, candidates, CppCandidateReason.Ambiguous);
}
