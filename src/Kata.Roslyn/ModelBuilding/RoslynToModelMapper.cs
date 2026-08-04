using Kata.Core.Model;
using Microsoft.CodeAnalysis;
using CoreAccessibility = Kata.Core.Model.MemberAccessibility;
using CoreTypeKind = Kata.Core.Model.TypeKind;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using RoslynTypeKind = Microsoft.CodeAnalysis.TypeKind;

namespace Kata.Roslyn.ModelBuilding;

internal static class RoslynToModelMapper
{
    private static readonly SymbolDisplayFormat TypeIdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat MemberSignatureFormat = new(
        memberOptions:
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat ReturnTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static TypeRef ToTypeRef(INamedTypeSymbol symbol)
        => new(symbol.ToDisplayString(TypeIdFormat));

    public static MemberRef ToMemberRef(ISymbol member)
        => new(ToTypeRef(member.ContainingType), member.ToDisplayString(MemberSignatureFormat));

    public static NamespaceRef ToNamespaceRef(INamespaceSymbol ns)
        => ns.IsGlobalNamespace ? NamespaceRef.Global : new NamespaceRef(ns.ToDisplayString());

    public static ProjectModel MapProject(Project project, Compilation compilation)
    {
        var types = new List<TypeModel>();
        var sourceAssembly = compilation.Assembly;
        CollectTypes(sourceAssembly.GlobalNamespace, types);
        return new ProjectModel(
            Name: project.Name,
            FilePath: project.FilePath ?? string.Empty,
            LanguageId: "csharp",
            Types: types);
    }

    private static void CollectTypes(INamespaceSymbol ns, List<TypeModel> sink)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol nested:
                    CollectTypes(nested, sink);
                    break;
                case INamedTypeSymbol type when ShouldIncludeType(type):
                    sink.Add(MapType(type));
                    foreach (var nestedType in type.GetTypeMembers())
                    {
                        if (ShouldIncludeType(nestedType))
                        {
                            sink.Add(MapType(nestedType));
                        }
                    }
                    break;
            }
        }
    }

    private static bool ShouldIncludeType(INamedTypeSymbol type)
    {
        if (type.IsImplicitlyDeclared)
        {
            return false;
        }

        if (type.Name.StartsWith("<", StringComparison.Ordinal))
        {
            return false;
        }

        if (type.DeclaringSyntaxReferences.Length == 0)
        {
            return false;
        }

        if (HasAttribute(type, "GeneratedCodeAttribute") ||
            HasAttribute(type, "CompilerGeneratedAttribute"))
        {
            return false;
        }

        return HasAnyHandWrittenSource(type);
    }

    private static bool HasAnyHandWrittenSource(ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var path = reference.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }

            if (!IsGeneratedSourcePath(path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGeneratedSourcePath(string path)
    {
        if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (path.IndexOf(@"\.nuget\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf("/.nuget/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf(@"\packages\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf("/packages/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (path.IndexOf("AssemblyAttributes", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private static TypeModel MapType(INamedTypeSymbol type)
    {
        var members = new List<MemberModel>();
        foreach (var member in type.GetMembers())
        {
            if (!ShouldIncludeMember(member))
            {
                continue;
            }

            members.Add(MapMember(member));
        }

        var baseTypes = type.BaseType is { SpecialType: not SpecialType.System_Object } baseType
            ? new[] { ToTypeRef(baseType) }
            : Array.Empty<TypeRef>();

        var interfaces = type.Interfaces
            .Select(ToTypeRef)
            .ToArray();

        return new TypeModel(
            Ref: ToTypeRef(type),
            Name: type.Name,
            Namespace: ToNamespaceRef(type.ContainingNamespace),
            Kind: MapTypeKind(type),
            Accessibility: MapAccessibility(type.DeclaredAccessibility),
            Members: members,
            BaseTypes: baseTypes,
            ImplementedInterfaces: interfaces,
            IsAbstract: type.IsAbstract,
            IsStatic: type.IsStatic);
    }

    private static bool ShouldIncludeMember(ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
        {
            return false;
        }

        if (member.DeclaringSyntaxReferences.Length > 0 && !HasAnyHandWrittenSource(member))
        {
            return false;
        }

        if (member is IFieldSymbol field)
        {
            // [ObservableProperty] backing field は source-authored な "member" として扱う。
            // 元は「property と backing field で重複表示を避ける」目的で filter していたが、
            // 生成 property 側は HasAnyHandWrittenSource (obj/ 配下は generated 扱い) で
            // 既に消えているので、重複は起きない。Extract Class で移動する原子単位は
            // backing field 側 (attribute が field に付いているので、field を移動すれば
            // 生成 property は移動先で再生成される)。よってここで backing field を消す
            // と、ObservableProperty が Extract Class から見えず操作できなくなる。
            // TODO: 表示名を underscore なしの PascalCase (ImpactFocusStatus) に整形すると
            //       もっと UX が良い。今は backing 名 (_impactFocusStatus) で表示。

            // auto-property の compiler-generated backing field
            // (<Foo>k__BackingField) は AssociatedSymbol を持つので引き続き除外する。
            if (field.AssociatedSymbol is IPropertySymbol or IEventSymbol)
            {
                return false;
            }
        }

        if (member is IMethodSymbol method)
        {
            if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
                or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise)
            {
                return false;
            }

            if (method.IsPartialDefinition && method.PartialImplementationPart is null)
            {
                return false;
            }
        }

        return member.Kind is
            SymbolKind.Field or
            SymbolKind.Property or
            SymbolKind.Method or
            SymbolKind.Event;
    }

    private static bool HasAttribute(ISymbol symbol, string attributeShortName)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == attributeShortName)
            {
                return true;
            }
        }

        return false;
    }

    private static MemberModel MapMember(ISymbol member)
    {
        var returnType = member switch
        {
            IMethodSymbol m => m.ReturnType.ToDisplayString(ReturnTypeFormat),
            IPropertySymbol p => p.Type.ToDisplayString(ReturnTypeFormat),
            IFieldSymbol f => f.Type.ToDisplayString(ReturnTypeFormat),
            IEventSymbol e => e.Type.ToDisplayString(ReturnTypeFormat),
            _ => string.Empty,
        };

        var parameters = member switch
        {
            IMethodSymbol m => m.Parameters
                .Select(p => new ParameterModel(p.Name, p.Type.ToDisplayString(ReturnTypeFormat)))
                .ToArray(),
            IPropertySymbol p when p.IsIndexer => p.Parameters
                .Select(pp => new ParameterModel(pp.Name, pp.Type.ToDisplayString(ReturnTypeFormat)))
                .ToArray(),
            _ => Array.Empty<ParameterModel>(),
        };

        var isReadOnly = member switch
        {
            IFieldSymbol f => f.IsReadOnly || f.IsConst,
            IPropertySymbol p => p.SetMethod is null || p.SetMethod.IsInitOnly,
            _ => false,
        };

        return new MemberModel(
            Ref: ToMemberRef(member),
            Name: member.Name,
            Kind: MapMemberKind(member),
            Accessibility: MapAccessibility(member.DeclaredAccessibility),
            ReturnTypeDisplay: returnType,
            IsStatic: member.IsStatic,
            Parameters: parameters,
            IsReadOnly: isReadOnly);
    }

    private static CoreTypeKind MapTypeKind(INamedTypeSymbol type) => type.TypeKind switch
    {
        RoslynTypeKind.Class => type.IsRecord ? CoreTypeKind.Record : CoreTypeKind.Class,
        RoslynTypeKind.Interface => CoreTypeKind.Interface,
        RoslynTypeKind.Struct => type.IsRecord ? CoreTypeKind.Record : CoreTypeKind.Struct,
        RoslynTypeKind.Enum => CoreTypeKind.Enum,
        RoslynTypeKind.Delegate => CoreTypeKind.Delegate,
        _ => CoreTypeKind.Unknown,
    };

    private static MemberKind MapMemberKind(ISymbol member) => member switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } => MemberKind.Constructor,
        IMethodSymbol => MemberKind.Method,
        IPropertySymbol { IsIndexer: true } => MemberKind.Indexer,
        IPropertySymbol => MemberKind.Property,
        IFieldSymbol => MemberKind.Field,
        IEventSymbol => MemberKind.Event,
        _ => MemberKind.Unknown,
    };

    private static CoreAccessibility MapAccessibility(RoslynAccessibility accessibility) => accessibility switch
    {
        RoslynAccessibility.Public => CoreAccessibility.Public,
        RoslynAccessibility.Internal => CoreAccessibility.Internal,
        RoslynAccessibility.Protected => CoreAccessibility.Protected,
        RoslynAccessibility.ProtectedOrInternal => CoreAccessibility.ProtectedInternal,
        RoslynAccessibility.ProtectedAndInternal => CoreAccessibility.PrivateProtected,
        RoslynAccessibility.Private => CoreAccessibility.Private,
        _ => CoreAccessibility.Private,
    };
}
