using Kata.Core.Model;
using Kata.Cpp.Syntax;

namespace Kata.Cpp;

public static class CppCliProjectParser
{
    public static IReadOnlyList<TypeModel> ParseProject(string vcxprojPath)
    {
        var headers = CppCliProjectLoader.EnumerateHeaders(vcxprojPath);
        var byRef = new Dictionary<string, CppDeclaration>(StringComparer.Ordinal);

        foreach (var header in headers)
        {
            string text;
            try
            {
                text = File.ReadAllText(header);
            }
            catch
            {
                continue;
            }

            var tokens = CppCliLexer.Tokenize(text);
            var decls = CppCliDeclParser.Parse(tokens);
            foreach (var d in decls)
            {
                var fqn = string.IsNullOrEmpty(d.NamespaceFullName)
                    ? d.Name
                    : $"{d.NamespaceFullName}.{d.Name}";
                byRef[fqn] = d;
            }
        }

        var types = new List<TypeModel>(byRef.Count);
        foreach (var (fqn, d) in byRef)
        {
            var baseTypes = d.BaseTypeNames
                .Select(n => ResolveTypeRef(n, byRef))
                .ToList();
            var interfaces = d.InterfaceTypeNames
                .Select(n => ResolveTypeRef(n, byRef))
                .ToList();

            var typeRef = new TypeRef(fqn);
            var members = new List<MemberModel>(d.Members.Count);
            foreach (var m in d.Members)
            {
                var parameters = m.Parameters ?? Array.Empty<CppParameter>();
                var returnTypeDisplay = m.ReturnTypeDisplay ?? string.Empty;
                var signature = m.Kind switch
                {
                    MemberKind.Method => SymbolKeyFormatter.FormatMethodSignature(
                        returnTypeDisplay,
                        m.Name,
                        parameters.Select(p => new SymbolKeyFormatter.ParameterKey(p.Type, p.Name)).ToArray()),
                    MemberKind.Constructor => SymbolKeyFormatter.FormatMethodSignature(
                        returnTypeDisplay: string.Empty,
                        m.Name,
                        parameters.Select(p => new SymbolKeyFormatter.ParameterKey(p.Type, p.Name)).ToArray()),
                    _ => SymbolKeyFormatter.FormatFieldSignature(m.Name),
                };
                var modelParameters = parameters
                    .Select(p => new ParameterModel(p.Name, p.Type))
                    .ToArray();
                members.Add(new MemberModel(
                    Ref: new MemberRef(typeRef, signature),
                    Name: m.Name,
                    Kind: m.Kind,
                    Accessibility: m.Accessibility,
                    ReturnTypeDisplay: returnTypeDisplay,
                    IsStatic: m.IsStatic,
                    Parameters: modelParameters));
            }

            types.Add(new TypeModel(
                Ref: typeRef,
                Name: d.Name,
                Namespace: new NamespaceRef(d.NamespaceFullName),
                Kind: d.Kind,
                Accessibility: MemberAccessibility.Public,
                Members: members,
                BaseTypes: baseTypes,
                ImplementedInterfaces: interfaces,
                IsAbstract: d.IsAbstract,
                IsStatic: false, // C++/CLI に "static class" 概念は無い (ref class の全 static は判定困難)
                IsGhost: false,
                IsForeignProject: false));
        }
        return types;
    }

    private static TypeRef ResolveTypeRef(string cppTypeName, IReadOnlyDictionary<string, CppDeclaration> localTypes)
    {
        // Convert C++ '::' separator to '.' first so we can match FQNs.
        var dotted = cppTypeName.Replace("::", ".");

        if (localTypes.ContainsKey(dotted))
        {
            return new TypeRef(dotted);
        }
        // Simple-name fallback: find any local type whose Name matches.
        var short_ = dotted.Split('.').Last();
        var match = localTypes.Values.FirstOrDefault(d => d.Name == short_);
        if (match is not null)
        {
            var ns = match.NamespaceFullName;
            return new TypeRef(string.IsNullOrEmpty(ns) ? match.Name : $"{ns}.{match.Name}");
        }
        // External type — leave dotted name as-is.
        return new TypeRef(dotted);
    }

}
