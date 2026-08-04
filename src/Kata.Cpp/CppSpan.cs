namespace Kata.Cpp;

public readonly record struct CppSpan(int Start, int Length, int Line)
{
    public int End => Start + Length;
    public bool IsEmpty => Length == 0 && Start == 0 && Line == 0;
}
