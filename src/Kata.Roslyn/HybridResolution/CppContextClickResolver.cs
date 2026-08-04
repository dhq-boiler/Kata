using Kata.Core.Model;
using Kata.Cpp;
using Kata.Cpp.Semantics;

namespace Kata.Roslyn.HybridResolution;

/// <summary>
/// Resolves a Ctrl+Click that happened while the code viewer was showing a
/// C++/CLI header or source file. The host type ("what member the viewer is
/// currently focused on") itself lives in the Cpp compilation, so Roslyn has
/// nothing to say — we go straight to token-level identifier lookup.
/// </summary>
public static class CppContextClickResolver
{
    public readonly record struct Result(TypeRef OwnerType, MemberRef Member, string DiagnosticSummary);

    public static Result? TryResolve(
        CppCompilation cpp,
        TypeRef contextOwnerType,
        MemberRef contextMember,
        int offsetInSource,
        string typeSiteSignature,
        out string failureReason)
    {
        failureReason = string.Empty;

        var hostType = cpp.GetTypeByFullyQualifiedName(contextOwnerType.FullyQualifiedName);
        if (hostType is null)
        {
            var similar = cpp.AllTypes
                .Where(t => string.Equals(t.Name, contextOwnerType.FullyQualifiedName.Split('.')[^1], StringComparison.Ordinal))
                .Select(t => t.FullyQualifiedName)
                .Take(3)
                .ToArray();
            var similarPart = similar.Length == 0 ? "no-similar" : $"similar=[{string.Join(",", similar)}]";
            failureReason = $"cpp-host-missing (indexed={cpp.AllTypes.Count} {similarPart})";
            return null;
        }

        // Type-site sentinel: viewer is currently showing the type's own declaration.
        var isTypeSite = string.Equals(contextMember.Signature, typeSiteSignature, StringComparison.Ordinal);
        CppMemberSymbol? hostMember = null;
        string siteFilePath;
        if (isTypeSite)
        {
            siteFilePath = hostType.DeclarationSite.FilePath;
        }
        else
        {
            hostMember = hostType.Members.FirstOrDefault(
                m => string.Equals(m.Signature, contextMember.Signature, StringComparison.Ordinal));
            if (hostMember is null)
            {
                var sample = hostType.Members.Take(4).Select(m => m.Signature);
                failureReason = $"cpp-host-member-mismatch (want='{contextMember.Signature}' host-has=[{string.Join(",", sample)}])";
                return null;
            }
            siteFilePath = hostMember.ImplementationSite?.FilePath ?? hostMember.DeclarationSite.FilePath;
        }
        var tree = cpp.ImplementationTrees
            .Concat(cpp.SyntaxTrees)
            .FirstOrDefault(t => string.Equals(t.FilePath, siteFilePath, StringComparison.OrdinalIgnoreCase));
        if (tree is null)
        {
            failureReason = $"cpp-tree-missing (site={siteFilePath})";
            return null;
        }

        // The viewer shows the whole .cpp file — the click may be inside a sibling
        // method (e.g. viewer opened on Connect but user scrolled to AttachProcessors).
        // Re-locate the actual enclosing method so InferReceiverType uses the right
        // Parameters / body scope.
        if (!isTypeSite)
        {
            var enclosing = FindMemberContainingOffset(hostType, tree, offsetInSource, hostMember);
            if (enclosing is not null)
            {
                hostMember = enclosing;
            }
        }

        var tokenIndex = FindIdentifierIndexAt(tree.Tokens, offsetInSource);
        if (tokenIndex < 0)
        {
            failureReason = $"cpp-token-not-identifier (offset={offsetInSource})";
            return null;
        }
        var token = tree.Tokens[tokenIndex];

        // When the click sits after a receiver-op (`x->name` or `x.name`), the intent is
        // a member access. Try member resolution BEFORE type resolution — otherwise a type
        // sharing the name (e.g. an "EqualizerProcessor" class vs a Handle.EqualizerProcessor
        // property) would win and misdirect the jump.
        var isMemberAccess = tokenIndex >= 2
            && tree.Tokens[tokenIndex - 1].Kind == CppTokenKind.Punctuation
            && tree.Tokens[tokenIndex - 1].Text is "->" or "."
            && tree.Tokens[tokenIndex - 2].Kind == CppTokenKind.Identifier;

        string memberFailure = string.Empty;
        if (isMemberAccess)
        {
            var memberResult = TryResolveMemberCall(cpp, tree.Tokens, tokenIndex, hostType, hostMember, out memberFailure);
            if (memberResult is not null)
            {
                return memberResult;
            }
            // Member access failed — do NOT fall through to type resolution. A same-named
            // type would otherwise steal the click (e.g. `handle->EqualizerProcessor` where
            // an `EqualizerProcessor` class also exists) and misdirect the jump.
            failureReason = !string.IsNullOrEmpty(memberFailure)
                ? memberFailure
                : $"cpp-member-not-found '{token.Text}' on receiver";
            return null;
        }

        // Bare identifier — try type resolution.
        var typeInfo = cpp.ResolveType(token.Text);
        var resolvedType = typeInfo.Symbol ?? typeInfo.CandidateSymbols.FirstOrDefault();
        if (resolvedType is not null)
        {
            var typeRef = new TypeRef(resolvedType.FullyQualifiedName);
            var memberRef = new MemberRef(typeRef, typeSiteSignature);
            var diag = $"Ctrl+Click via Kata.Cpp [type]: {resolvedType.FullyQualifiedName}";
            return new Result(typeRef, memberRef, diag);
        }

        // Bare identifier without a preceding receiver — try implicit-this method call.
        if (!isMemberAccess)
        {
            var memberResult = TryResolveMemberCall(cpp, tree.Tokens, tokenIndex, hostType, hostMember, out memberFailure);
            if (memberResult is not null)
            {
                return memberResult;
            }

            // File-level function fallback: `EnrollProcessor(...)` at the top of the
            // impl file, not a member of the host type.
            if (cpp.FileFunctionsByFilePath.TryGetValue(tree.FilePath, out var fns))
            {
                var fnCandidates = fns.Where(f =>
                    string.Equals(f.Name, token.Text, StringComparison.Ordinal)).ToArray();
                var arity = TryCountCallArgs(tree.Tokens, tokenIndex);
                var fn = fnCandidates.Length == 1
                    ? fnCandidates[0]
                    : fnCandidates.FirstOrDefault(f => arity is null || f.ParameterCount == arity.Value)
                      ?? fnCandidates.FirstOrDefault();
                if (fn is not null)
                {
                    var syntheticType = new TypeRef($"{FileFunctionOwnerPrefix}{tree.FilePath}>");
                    var syntheticMember = new MemberRef(syntheticType, $"{FileFunctionSignaturePrefix}{fn.Name}");
                    var diag = $"Ctrl+Click via Kata.Cpp [file-fn]: {fn.Name}";
                    return new Result(syntheticType, syntheticMember, diag);
                }
            }

            // Macro fallback: preprocessor macros are typically #defined in pch/util
            // headers and referenced from any caller file, so search across ALL files
            // rather than only the current tree. Name-only match — macros have no
            // proper arity in the type system, and function-like macros could still be
            // "called" with any number of args due to __VA_ARGS__.
            foreach (var (macFilePath, macros) in cpp.FileMacrosByFilePath)
            {
                var macMatch = macros.FirstOrDefault(m =>
                    string.Equals(m.Name, token.Text, StringComparison.Ordinal));
                if (macMatch is not null)
                {
                    var syntheticType = new TypeRef($"{FileFunctionOwnerPrefix}{macFilePath}>");
                    var syntheticMember = new MemberRef(syntheticType, $"{MacroSignaturePrefix}{macMatch.Name}");
                    var diag = $"Ctrl+Click via Kata.Cpp [macro]: {macMatch.Name}";
                    return new Result(syntheticType, syntheticMember, diag);
                }
            }
        }

        failureReason = !string.IsNullOrEmpty(memberFailure)
            ? memberFailure
            : $"cpp-type-not-resolved '{token.Text}'";
        return null;
    }

    /// <summary>
    /// Prefix used in a synthetic <see cref="TypeRef"/>'s FullyQualifiedName to signal
    /// that the click resolved to a file-level function rather than a member of a type.
    /// The full form is "&lt;file-fn:{filepath}&gt;".
    /// </summary>
    public const string FileFunctionOwnerPrefix = "<file-fn:";

    /// <summary>
    /// Prefix used in a synthetic <see cref="MemberRef"/>'s Signature to carry the file
    /// function's name. The full form is "&lt;file-fn&gt;Name".
    /// </summary>
    public const string FileFunctionSignaturePrefix = "<file-fn>";

    /// <summary>
    /// Prefix used in a synthetic <see cref="MemberRef"/>'s Signature to carry a
    /// preprocessor macro's name. The full form is "&lt;macro&gt;NAME". Reuses
    /// <see cref="FileFunctionOwnerPrefix"/> for the owning TypeRef because macros
    /// share the same file-scope pseudo type as file functions.
    /// </summary>
    public const string MacroSignaturePrefix = "<macro>";

    private static Result? TryResolveMemberCall(
        CppCompilation cpp,
        IReadOnlyList<CppToken> tokens,
        int methodIdx,
        CppTypeSymbol hostType,
        CppMemberSymbol? hostMember,
        out string failure)
    {
        failure = string.Empty;

        var hasCallParen = methodIdx + 1 < tokens.Count
            && tokens[methodIdx + 1].Kind == CppTokenKind.Punctuation
            && tokens[methodIdx + 1].Text == "(";

        var hasReceiverOp = methodIdx >= 2
            && tokens[methodIdx - 1].Kind == CppTokenKind.Punctuation
            && tokens[methodIdx - 1].Text is "->" or "."
            && tokens[methodIdx - 2].Kind == CppTokenKind.Identifier;

        // Nothing to try: bare identifier without a receiver and without a call.
        if (!hasCallParen && !hasReceiverOp)
        {
            return null;
        }

        CppTypeSymbol? receiverType;
        var receiverLabel = "implicit-this";

        if (hasReceiverOp)
        {
            var receiver = tokens[methodIdx - 2];
            receiverLabel = receiver.Text;
            receiverType = InferReceiverType(cpp, receiver.Text, hostType, hostMember, tokens, methodIdx);
            if (receiverType is null)
            {
                failure = $"cpp-method-receiver-unknown (receiver='{receiver.Text}', method='{tokens[methodIdx].Text}')";
                return null;
            }
        }
        else
        {
            // Bare `method(...)` — implicit-this call on the host type.
            receiverType = hostType;
        }

        var methodName = tokens[methodIdx].Text;
        var arity = hasCallParen ? TryCountCallArgs(tokens, methodIdx) : null;
        var memberInfo = cpp.ResolveMember(receiverType, methodName, arity);
        var matched = memberInfo.Symbol ?? memberInfo.CandidateSymbols.FirstOrDefault();
        if (matched is null && arity is not null)
        {
            // Arity inference is best-effort — numeric-only args yield no tokens, so a call
            // like `Foo(42)` looks like arity 0. Fall back to a name-only lookup.
            var relaxed = cpp.ResolveMember(receiverType, methodName, arity: null);
            matched = relaxed.Symbol ?? relaxed.CandidateSymbols.FirstOrDefault();
        }
        if (matched is null)
        {
            failure = $"cpp-method-not-found (receiver={receiverLabel}, {receiverType.FullyQualifiedName}.{methodName}, arity={arity})";
            return null;
        }

        var typeRef = new TypeRef(receiverType.FullyQualifiedName);
        var memberRef = new MemberRef(typeRef, matched.Signature);
        var siteKind = matched.ImplementationSite is not null ? "impl" : "decl";
        var diag = $"Ctrl+Click via Kata.Cpp [{siteKind}]: {receiverType.FullyQualifiedName}.{matched.Name}";
        return new Result(typeRef, memberRef, diag);
    }

    private static CppTypeSymbol? InferReceiverType(
        CppCompilation cpp,
        string receiverName,
        CppTypeSymbol hostType,
        CppMemberSymbol? hostMember,
        IReadOnlyList<CppToken> tokens,
        int clickTokenIdx,
        int depth = 0)
    {
        const int MaxChainDepth = 6;
        if (depth > MaxChainDepth) return null;

        // "this" — receiver is the host type itself.
        if (string.Equals(receiverName, "this", StringComparison.Ordinal))
        {
            return hostType;
        }

        // Method parameter.
        if (hostMember is not null)
        {
            var param = hostMember.Parameters.FirstOrDefault(
                p => string.Equals(p.Name, receiverName, StringComparison.Ordinal));
            if (param is not null)
            {
                var normalized = SymbolKeyFormatter.NormalizeCppTypeName(param.Type);
                var info = cpp.ResolveType(normalized);
                var resolved = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (resolved is not null) return resolved;
            }
        }

        // Host-type field.
        var field = hostType.Members.FirstOrDefault(
            m => m.Kind == MemberKind.Field
                 && string.Equals(m.Name, receiverName, StringComparison.Ordinal));
        if (field is not null)
        {
            var normalized = SymbolKeyFormatter.NormalizeCppTypeName(field.ReturnTypeDisplay);
            var info = cpp.ResolveType(normalized);
            var resolved = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (resolved is not null) return resolved;
        }

        // Local variable declared inside the current method body.
        var bodyStart = FindFunctionBodyStart(tokens, hostMember?.ImplementationSite?.Span.Start);
        var localTypeName = TryInferLocalVarType(cpp, tokens, receiverName, clickTokenIdx, bodyStart, hostType, hostMember);
        if (localTypeName is not null)
        {
            var info = cpp.ResolveType(localTypeName);
            var resolved = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (resolved is not null) return resolved;
        }

        // Chained access: `parent -> receiverName` — receiverName is a property/field/method
        // on some parent expression. Recursively infer the parent's type, then look up
        // receiverName as a member and use its return-type.
        var receiverIdx = clickTokenIdx - 2;
        if (receiverIdx >= 2
            && tokens[receiverIdx - 1].Kind == CppTokenKind.Punctuation
            && tokens[receiverIdx - 1].Text is "->" or "."
            && tokens[receiverIdx - 2].Kind == CppTokenKind.Identifier)
        {
            var parentName = tokens[receiverIdx - 2].Text;
            var parentType = InferReceiverType(cpp, parentName, hostType, hostMember, tokens, receiverIdx, depth + 1);
            if (parentType is not null)
            {
                var member = parentType.Members.FirstOrDefault(
                    m => string.Equals(m.Name, receiverName, StringComparison.Ordinal));
                if (member is not null && !string.IsNullOrEmpty(member.ReturnTypeDisplay))
                {
                    var normalized = SymbolKeyFormatter.NormalizeCppTypeName(member.ReturnTypeDisplay);
                    var info = cpp.ResolveType(normalized);
                    var resolved = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (resolved is not null) return resolved;
                }
            }
        }

        return null;
    }

    private static int FindFunctionBodyStart(IReadOnlyList<CppToken> tokens, int? methodNamePosition)
    {
        if (methodNamePosition is null) return 0;
        // Find the method-name token, then walk forward to the first '{' at the top level
        // that follows its argument list — that's the function body opener.
        var methodIdx = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Position == methodNamePosition.Value)
            {
                methodIdx = i;
                break;
            }
        }
        if (methodIdx < 0) return 0;
        for (var i = methodIdx + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "{")
            {
                return i;
            }
            if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ";")
            {
                return 0; // declaration only (no body)
            }
        }
        return 0;
    }

    private static string? TryInferLocalVarType(
        CppCompilation cpp,
        IReadOnlyList<CppToken> tokens,
        string receiverName,
        int clickTokenIdx,
        int functionBodyStartIdx,
        CppTypeSymbol hostType,
        CppMemberSymbol? hostMember)
    {
        // Walk backward from the click. Skip past sibling blocks ({...} we've exited)
        // but pass through enclosing-scope opens ('{') so we can see outer-scope locals.
        // Do not go earlier than the enclosing function's body opener.
        var declEnd = -1;
        var i = clickTokenIdx - 1;
        while (i > functionBodyStartIdx)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "}")
            {
                // Skip back over the matching {...} — an already-exited inner block.
                var depth = 1;
                var j = i - 1;
                while (j > functionBodyStartIdx && depth > 0)
                {
                    if (tokens[j].Kind == CppTokenKind.Punctuation)
                    {
                        if (tokens[j].Text == "}") depth++;
                        else if (tokens[j].Text == "{") depth--;
                    }
                    if (depth > 0) j--;
                }
                i = j - 1;
                continue;
            }
            if (t.Kind == CppTokenKind.Identifier && t.Text == receiverName
                && i + 1 < tokens.Count
                && tokens[i + 1].Kind == CppTokenKind.Punctuation
                && tokens[i + 1].Text is "=" or ";" or "(")
            {
                if (IsDeclarationOccurrence(tokens, i))
                {
                    declEnd = i;
                    break;
                }
            }
            i--;
        }
        if (declEnd < 0) return null;

        // Walk back from declEnd to a statement boundary to isolate the type-tokens.
        var declStart = 0;
        for (var j = declEnd - 1; j >= 0; j--)
        {
            var t = tokens[j];
            if (t.Kind == CppTokenKind.Punctuation
                && (t.Text is ";" or "{" or "}"))
            {
                declStart = j + 1;
                break;
            }
        }

        // Extract the "type" portion between declStart and declEnd.
        // If the first non-modifier identifier is "auto", chase the initializer for gcnew Type.
        var typeIdent = string.Empty;
        for (var k = declStart; k < declEnd; k++)
        {
            var t = tokens[k];
            if (t.Kind != CppTokenKind.Identifier) continue;
            if (t.Text is "const" or "volatile" or "static" or "mutable") continue;
            typeIdent = t.Text;
            break;
        }

        if (typeIdent == "auto")
        {
            return InferAutoInitializerType(cpp, tokens, declEnd, hostType, hostMember);
        }

        return string.IsNullOrEmpty(typeIdent) ? null : typeIdent;
    }

    /// <summary>
    /// Decide whether the identifier at <paramref name="idx"/> is likely a *declaration*
    /// of a local (rather than a comparison / re-assignment / call / RHS reference).
    /// A declaration always has some type-like token immediately before the name:
    /// an identifier (`Type`, `auto`, `const`, ...) or a type-sigil (`^`, `*`, `&`, `>`).
    /// </summary>
    private static bool IsDeclarationOccurrence(IReadOnlyList<CppToken> tokens, int idx)
    {
        var next = tokens[idx + 1];

        // Exclude `x == ...` comparison.
        if (next.Text == "=" && idx + 2 < tokens.Count
            && tokens[idx + 2].Kind == CppTokenKind.Punctuation
            && tokens[idx + 2].Text == "=")
        {
            return false;
        }

        if (idx == 0) return false;
        var prev = tokens[idx - 1];
        var prevLooksLikeType = prev.Kind == CppTokenKind.Identifier
            || (prev.Kind == CppTokenKind.Punctuation && prev.Text is "^" or "*" or "&" or ">");

        // For any suffix in `= / ; / (`, the identifier is a declaration only when preceded
        // by type-tokens. Otherwise it's a re-assignment, an RHS reference, a bare statement,
        // or a function call.
        return prevLooksLikeType;
    }

    private static string? InferAutoInitializerType(
        CppCompilation cpp,
        IReadOnlyList<CppToken> tokens,
        int declEnd,
        CppTypeSymbol hostType,
        CppMemberSymbol? hostMember)
    {
        if (declEnd + 1 >= tokens.Count
            || tokens[declEnd + 1].Kind != CppTokenKind.Punctuation
            || tokens[declEnd + 1].Text != "=")
        {
            return null;
        }

        var initStart = declEnd + 2;
        var initEnd = initStart;
        while (initEnd < tokens.Count
               && !(tokens[initEnd].Kind == CppTokenKind.Punctuation && tokens[initEnd].Text == ";"))
        {
            initEnd++;
        }

        // Pattern A: `gcnew <TypeName>` anywhere in the initializer.
        for (var k = initStart; k < initEnd; k++)
        {
            if (tokens[k].Kind == CppTokenKind.Identifier && tokens[k].Text == "gcnew"
                && k + 1 < initEnd && tokens[k + 1].Kind == CppTokenKind.Identifier)
            {
                return tokens[k + 1].Text;
            }
        }

        // Pattern A': `dynamic_cast<TypeName^>(expr)` / `static_cast<...>` /
        // `reinterpret_cast<...>` / `const_cast<...>` / `safe_cast<...>` (C++/CLI).
        if (initStart < initEnd
            && tokens[initStart].Kind == CppTokenKind.Identifier
            && tokens[initStart].Text is "dynamic_cast" or "static_cast"
                or "reinterpret_cast" or "const_cast" or "safe_cast"
            && initStart + 1 < initEnd
            && tokens[initStart + 1].Kind == CppTokenKind.Punctuation
            && tokens[initStart + 1].Text == "<")
        {
            for (var k = initStart + 2; k < initEnd; k++)
            {
                if (tokens[k].Kind == CppTokenKind.Punctuation && tokens[k].Text == ">") break;
                if (tokens[k].Kind == CppTokenKind.Identifier)
                {
                    return tokens[k].Text;
                }
            }
        }

        // Pattern B: implicit-this method call `<method>(...)` at the start of the initializer.
        if (initStart < initEnd
            && tokens[initStart].Kind == CppTokenKind.Identifier
            && initStart + 1 < initEnd
            && tokens[initStart + 1].Kind == CppTokenKind.Punctuation
            && tokens[initStart + 1].Text == "(")
        {
            var methodName = tokens[initStart].Text;
            var arity = TryCountCallArgs(tokens, initStart);
            var ret = ReturnTypeOfMethod(hostType, methodName, arity);
            if (ret is not null) return ret;
        }

        // Pattern C: `<receiver> -> <name>(args)` / `<receiver> . <name>(args)` (method call)
        //         or `<receiver> -> <name>`       / `<receiver> . <name>`       (property/field access).
        if (initStart + 2 < initEnd
            && tokens[initStart].Kind == CppTokenKind.Identifier
            && tokens[initStart + 1].Kind == CppTokenKind.Punctuation
            && tokens[initStart + 1].Text is "->" or "."
            && tokens[initStart + 2].Kind == CppTokenKind.Identifier)
        {
            var receiverName = tokens[initStart].Text;
            var methodName = tokens[initStart + 2].Text;
            var hasParen = initStart + 3 < initEnd
                && tokens[initStart + 3].Kind == CppTokenKind.Punctuation
                && tokens[initStart + 3].Text == "(";
            var arity = hasParen ? TryCountCallArgs(tokens, initStart + 2) : null;
            var receiverType = InferReceiverType(cpp, receiverName, hostType, hostMember, tokens, initStart + 2);
            if (receiverType is not null)
            {
                var ret = ReturnTypeOfMethod(receiverType, methodName, arity);
                if (ret is not null) return ret;
            }
        }

        // Pattern D: bare identifier at start of initializer = host-type field/property reference.
        if (initStart < initEnd
            && tokens[initStart].Kind == CppTokenKind.Identifier
            && (initStart + 1 >= initEnd
                || tokens[initStart + 1].Kind != CppTokenKind.Punctuation
                || tokens[initStart + 1].Text is ";" or "," or ")"))
        {
            var name = tokens[initStart].Text;
            var member = hostType.Members.FirstOrDefault(
                m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (member is not null && !string.IsNullOrEmpty(member.ReturnTypeDisplay))
            {
                return SymbolKeyFormatter.NormalizeCppTypeName(member.ReturnTypeDisplay);
            }
        }

        // Pattern F: `<receiver>[<expr>]` — indexer / subscript. When receiver has a
        // generic container type `Type<T>[^]`, the element type is T.
        if (initStart + 1 < initEnd
            && tokens[initStart].Kind == CppTokenKind.Identifier
            && tokens[initStart + 1].Kind == CppTokenKind.Punctuation
            && tokens[initStart + 1].Text == "[")
        {
            var receiverName = tokens[initStart].Text;
            var receiverTypeStr = TryFindReceiverTypeString(tokens, receiverName, initStart, hostType, hostMember);
            if (receiverTypeStr is not null)
            {
                var elementType = TryExtractFirstTemplateArg(receiverTypeStr);
                if (elementType is not null) return elementType;
            }
        }

        return null;
    }

    private static string? TryFindReceiverTypeString(
        IReadOnlyList<CppToken> tokens,
        string receiverName,
        int clickTokenIdx,
        CppTypeSymbol hostType,
        CppMemberSymbol? hostMember)
    {
        // Parameter.
        if (hostMember is not null)
        {
            var p = hostMember.Parameters.FirstOrDefault(
                x => string.Equals(x.Name, receiverName, StringComparison.Ordinal));
            if (p is not null) return p.Type;
        }

        // Host-type field.
        var field = hostType.Members.FirstOrDefault(
            m => m.Kind == MemberKind.Field
                 && string.Equals(m.Name, receiverName, StringComparison.Ordinal));
        if (field is not null && !string.IsNullOrEmpty(field.ReturnTypeDisplay))
        {
            return field.ReturnTypeDisplay;
        }

        // Local declaration inside the current method body.
        var bodyStart = FindFunctionBodyStart(tokens, hostMember?.ImplementationSite?.Span.Start);
        var (declStart, declEnd) = FindLocalDeclarationRange(tokens, receiverName, clickTokenIdx, bodyStart);
        if (declStart >= 0 && declEnd > declStart)
        {
            var toks = new List<string>();
            for (var k = declStart; k < declEnd; k++)
            {
                var t = tokens[k];
                if (t.Kind == CppTokenKind.Identifier
                    || (t.Kind == CppTokenKind.Punctuation
                        && t.Text is "<" or ">" or "^" or "*" or "&" or "::" or ","))
                {
                    toks.Add(t.Text);
                }
            }
            return string.Join(" ", toks);
        }
        return null;
    }

    private static (int Start, int End) FindLocalDeclarationRange(
        IReadOnlyList<CppToken> tokens,
        string receiverName,
        int clickTokenIdx,
        int functionBodyStartIdx)
    {
        var declEnd = -1;
        var i = clickTokenIdx - 1;
        while (i > functionBodyStartIdx)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "}")
            {
                var depth = 1;
                var j = i - 1;
                while (j > functionBodyStartIdx && depth > 0)
                {
                    if (tokens[j].Kind == CppTokenKind.Punctuation)
                    {
                        if (tokens[j].Text == "}") depth++;
                        else if (tokens[j].Text == "{") depth--;
                    }
                    if (depth > 0) j--;
                }
                i = j - 1;
                continue;
            }
            if (t.Kind == CppTokenKind.Identifier && t.Text == receiverName
                && i + 1 < tokens.Count
                && tokens[i + 1].Kind == CppTokenKind.Punctuation
                && tokens[i + 1].Text is "=" or ";" or "(")
            {
                if (IsDeclarationOccurrence(tokens, i))
                {
                    declEnd = i;
                    break;
                }
            }
            i--;
        }
        if (declEnd < 0) return (-1, -1);

        var declStart = 0;
        for (var j = declEnd - 1; j >= 0; j--)
        {
            var t = tokens[j];
            if (t.Kind == CppTokenKind.Punctuation && t.Text is ";" or "{" or "}")
            {
                declStart = j + 1;
                break;
            }
        }
        return (declStart, declEnd);
    }

    private static string? TryExtractFirstTemplateArg(string typeString)
    {
        var open = typeString.IndexOf('<');
        if (open < 0) return null;
        var depth = 1;
        var i = open + 1;
        var start = i;
        while (i < typeString.Length)
        {
            var c = typeString[i];
            if (c == '<') { depth++; }
            else if (c == '>')
            {
                depth--;
                if (depth == 0) break;
            }
            else if (c == ',' && depth == 1)
            {
                break;
            }
            i++;
        }
        if (i >= typeString.Length) return null;
        var arg = typeString.Substring(start, i - start).Trim();
        return string.IsNullOrEmpty(arg) ? null : SymbolKeyFormatter.NormalizeCppTypeName(arg);
    }

    private static string? ReturnTypeOfMethod(CppTypeSymbol type, string methodName, int? arity)
    {
        var candidates = type.Members.Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal)).ToArray();
        CppMemberSymbol? matched = candidates.Length switch
        {
            0 => null,
            1 => candidates[0],
            _ => candidates.FirstOrDefault(m => arity is null || m.Parameters.Count == arity.Value)
                 ?? candidates[0],
        };
        if (matched is null || string.IsNullOrEmpty(matched.ReturnTypeDisplay))
        {
            return null;
        }
        return SymbolKeyFormatter.NormalizeCppTypeName(matched.ReturnTypeDisplay);
    }

    private static int? TryCountCallArgs(IReadOnlyList<CppToken> tokens, int methodIdx)
    {
        if (methodIdx + 1 >= tokens.Count) return null;
        if (tokens[methodIdx + 1].Kind != CppTokenKind.Punctuation || tokens[methodIdx + 1].Text != "(")
        {
            return null;
        }

        var depth = 0;
        var commas = 0;
        var hasContent = false;
        for (var i = methodIdx + 2; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation)
            {
                if (t.Text is "(" or "<") depth++;
                else if (t.Text is ")" or ">")
                {
                    if (depth == 0)
                    {
                        return hasContent ? commas + 1 : 0;
                    }
                    depth--;
                    continue;
                }
                else if (t.Text == "," && depth == 0)
                {
                    commas++;
                    hasContent = true; // comma implies at least two arguments on either side
                    continue;
                }
            }
            hasContent = true;
        }
        return null;
    }

    private static CppMemberSymbol? FindMemberContainingOffset(
        CppTypeSymbol hostType,
        Kata.Cpp.Syntax.CppSyntaxTree tree,
        int offset,
        CppMemberSymbol? defaultMember)
    {
        var tokens = tree.Tokens;
        var candidates = hostType.Members
            .Where(m => m.ImplementationSite is { } s
                     && string.Equals(s.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var m in candidates)
        {
            var implPos = m.ImplementationSite!.Value.Span.Start;
            var methodNameIdx = -1;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Position == implPos)
                {
                    methodNameIdx = i;
                    break;
                }
            }
            if (methodNameIdx < 0) continue;

            var openBrace = -1;
            for (var i = methodNameIdx + 1; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "{")
                {
                    openBrace = i;
                    break;
                }
                if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ";")
                {
                    break;
                }
            }
            if (openBrace < 0) continue;

            var depth = 1;
            var closeBraceIdx = -1;
            for (var i = openBrace + 1; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != CppTokenKind.Punctuation) continue;
                if (tokens[i].Text == "{") depth++;
                else if (tokens[i].Text == "}")
                {
                    depth--;
                    if (depth == 0) { closeBraceIdx = i; break; }
                }
            }
            if (closeBraceIdx < 0) continue;

            var openPos = tokens[openBrace].Position;
            var closePos = tokens[closeBraceIdx].Position + tokens[closeBraceIdx].Length;
            if (offset >= openPos && offset <= closePos)
            {
                return m;
            }
        }

        return defaultMember;
    }

    private static int FindIdentifierIndexAt(IReadOnlyList<CppToken> tokens, int offset)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != CppTokenKind.Identifier) continue;
            if (t.Position <= offset && offset < t.Position + t.Length)
            {
                return i;
            }
        }
        return -1;
    }
}
