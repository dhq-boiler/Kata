namespace Kata.Cpp.Semantics;

public sealed class CppSymbolInfo
{
    public static CppSymbolInfo NotFound { get; } =
        new(null, Array.Empty<CppTypeSymbol>(), CppCandidateReason.NotFound);

    public CppTypeSymbol? Symbol { get; }
    public IReadOnlyList<CppTypeSymbol> CandidateSymbols { get; }
    public CppCandidateReason CandidateReason { get; }

    internal CppSymbolInfo(
        CppTypeSymbol? symbol,
        IReadOnlyList<CppTypeSymbol> candidateSymbols,
        CppCandidateReason candidateReason)
    {
        Symbol = symbol;
        CandidateSymbols = candidateSymbols;
        CandidateReason = candidateReason;
    }

    public static CppSymbolInfo Resolved(CppTypeSymbol symbol)
        => new(symbol, Array.Empty<CppTypeSymbol>(), CppCandidateReason.None);

    public static CppSymbolInfo Ambiguous(IReadOnlyList<CppTypeSymbol> candidates)
        => new(null, candidates, CppCandidateReason.Ambiguous);
}
