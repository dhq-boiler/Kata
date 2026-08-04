using Kata.Core.Model;

namespace Kata.Core.Analysis;

// A single-purpose analyzer for one SmellCategory. Adapters register a set of these and the
// aggregator runs them in sequence, concatenating the produced smells into a SmellIndex.
// Detectors receive a SmellDetectionContext (adapter-specific payload) via the concrete
// analyzer host — this interface intentionally stays payload-agnostic so Core has no
// dependency on Roslyn or any other language stack.
public interface ICodeSmellDetector
{
    SmellCategory Category { get; }
}
