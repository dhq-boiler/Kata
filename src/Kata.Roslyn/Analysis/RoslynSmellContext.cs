using Kata.Core.Analysis;
using Kata.Core.Model;
using Kata.Roslyn.ModelBuilding;
using Microsoft.CodeAnalysis;
using MsSolution = Microsoft.CodeAnalysis.Solution;

namespace Kata.Roslyn.Analysis;

// Roslyn 側の smell context。<see cref="ISmellContext"/> を実装して universal detector に
// C# 型 / body を提供しつつ、Roslyn 固有 detector 用に Compilations と TypeSymbols も持つ。
// body / 型全文の取得は SyntaxTree からのテキスト抽出。
internal sealed class RoslynSmellContext : ISmellContext
{
    private readonly Dictionary<MemberRef, IMethodSymbol> _methodByRef;
    private readonly Dictionary<TypeRef, INamedTypeSymbol> _typeSymbols;
    private readonly Dictionary<TypeRef, TypeModel> _typeModels;

    private RoslynSmellContext(
        MsSolution solution,
        SolutionModel model,
        IReadOnlyList<Compilation> compilations,
        Dictionary<TypeRef, INamedTypeSymbol> typeSymbols,
        Dictionary<TypeRef, TypeModel> typeModels,
        Dictionary<MemberRef, IMethodSymbol> methodByRef)
    {
        Solution = solution;
        Model = model;
        Compilations = compilations;
        _typeSymbols = typeSymbols;
        _typeModels = typeModels;
        _methodByRef = methodByRef;
    }

    public MsSolution Solution { get; }
    public SolutionModel Model { get; }
    public string LanguageId => "csharp";
    public IReadOnlyList<Compilation> Compilations { get; }
    public IReadOnlyDictionary<TypeRef, INamedTypeSymbol> TypeSymbols => _typeSymbols;
    public IReadOnlyDictionary<TypeRef, TypeModel> TypeModels => _typeModels;

    public IEnumerable<TypeModel> HandwrittenTypes
    {
        get
        {
            foreach (var kv in _typeModels)
            {
                if (_typeSymbols.ContainsKey(kv.Key)) yield return kv.Value;
            }
        }
    }

    public string? GetBodyText(MemberRef member)
    {
        if (!_methodByRef.TryGetValue(member, out var method)) return null;
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var decl = syntaxRef.GetSyntax();
            var body = DetectorHelpers.GetMethodBody(decl);
            if (body is not null) return body.ToString();
        }
        return null;
    }

    public int GetBodyLineCount(MemberRef member)
    {
        if (!_methodByRef.TryGetValue(member, out var method)) return 0;
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var decl = syntaxRef.GetSyntax();
            var body = DetectorHelpers.GetMethodBody(decl);
            if (body is null) continue;
            return DetectorHelpers.LineCount(body);
        }
        return 0;
    }

    public string? GetTypeText(TypeRef type)
    {
        if (!_typeSymbols.TryGetValue(type, out var sym)) return null;
        var parts = sym.DeclaringSyntaxReferences.Select(r => r.GetSyntax().ToString());
        var joined = string.Join("\n", parts);
        return joined.Length == 0 ? null : joined;
    }

    public bool TryGetType(TypeRef typeRef, out TypeModel? type)
    {
        if (_typeModels.TryGetValue(typeRef, out var m)) { type = m; return true; }
        type = null;
        return false;
    }

    public static async Task<RoslynSmellContext> CreateAsync(
        MsSolution solution,
        SolutionModel model,
        CancellationToken ct)
    {
        var compilations = new List<Compilation>();
        var typeSymbols = new Dictionary<TypeRef, INamedTypeSymbol>();
        var methodByRef = new Dictionary<MemberRef, IMethodSymbol>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp) continue;

            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;
            compilations.Add(compilation);

            CollectTypes(compilation.Assembly.GlobalNamespace, typeSymbols, methodByRef);
        }

        var typeModels = new Dictionary<TypeRef, TypeModel>();
        foreach (var project in model.Projects)
            foreach (var type in project.Types)
                typeModels[type.Ref] = type;

        return new RoslynSmellContext(solution, model, compilations, typeSymbols, typeModels, methodByRef);
    }

    private static void CollectTypes(
        INamespaceSymbol ns,
        Dictionary<TypeRef, INamedTypeSymbol> typeSink,
        Dictionary<MemberRef, IMethodSymbol> methodSink)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol nested:
                    CollectTypes(nested, typeSink, methodSink);
                    break;
                case INamedTypeSymbol type:
                    typeSink[RoslynToModelMapper.ToTypeRef(type)] = type;
                    CollectMethods(type, methodSink);
                    foreach (var nestedType in type.GetTypeMembers())
                    {
                        typeSink[RoslynToModelMapper.ToTypeRef(nestedType)] = nestedType;
                        CollectMethods(nestedType, methodSink);
                    }
                    break;
            }
        }
    }

    private static void CollectMethods(INamedTypeSymbol type, Dictionary<MemberRef, IMethodSymbol> sink)
    {
        foreach (var m in type.GetMembers())
        {
            if (m is not IMethodSymbol method) continue;
            if (method.IsImplicitlyDeclared) continue;
            if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
                or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise) continue;
            if (method.DeclaringSyntaxReferences.Length == 0) continue;
            sink[RoslynToModelMapper.ToMemberRef(method)] = method;
        }
    }
}
