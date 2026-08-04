using Kata.Core.Model;

namespace Kata.Cpp;

public sealed record CppDeclaration(
    string Name,
    string NamespaceFullName,
    TypeKind Kind,
    IReadOnlyList<string> BaseTypeNames,
    IReadOnlyList<string> InterfaceTypeNames,
    IReadOnlyList<CppMember> Members,
    CppSpan NameSpan = default,
    bool IsAbstract = false,
    bool IsSealed = false);

public sealed record CppMember(
    string Name,
    MemberKind Kind,
    MemberAccessibility Accessibility,
    string ReturnTypeDisplay,
    bool IsStatic,
    IReadOnlyList<CppParameter>? Parameters = null,
    CppSpan NameSpan = default);

public sealed record CppParameter(string Type, string Name);

public static class CppCliDeclParser
{
    private static readonly HashSet<string> AccessSpecifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal",
    };

    private static readonly HashSet<string> KindModifiers = new(StringComparer.Ordinal)
    {
        "ref", "value", "interface", "enum",
    };

    public static IReadOnlyList<CppDeclaration> Parse(IReadOnlyList<CppToken> tokens)
    {
        var declarations = new List<CppDeclaration>();
        var namespaceStack = new Stack<List<string>>();
        var braceDepth = 0;
        var braceDepthAtNamespace = new Stack<(int Depth, int PushCount)>();

        var i = 0;
        while (i < tokens.Count && tokens[i].Kind != CppTokenKind.EndOfFile)
        {
            var t = tokens[i];

            // Attribute [[ ... ]] or [ ... ]
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "[")
            {
                i = SkipBalanced(tokens, i, "[", "]");
                continue;
            }

            // Track brace depth for non-class blocks (function bodies, initializer lists, etc.)
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "{")
            {
                braceDepth++;
                i++;
                continue;
            }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "}")
            {
                braceDepth--;
                i++;
                // Pop namespace if we closed its brace
                if (braceDepthAtNamespace.Count > 0 && braceDepthAtNamespace.Peek().Depth == braceDepth)
                {
                    braceDepthAtNamespace.Pop();
                    namespaceStack.Pop();
                }
                continue;
            }

            if (t.Kind == CppTokenKind.Identifier)
            {
                if (t.Text == "namespace")
                {
                    i = HandleNamespace(tokens, i + 1, namespaceStack, braceDepthAtNamespace, ref braceDepth);
                    continue;
                }

                if (t.Text == "template")
                {
                    i = SkipTemplate(tokens, i + 1);
                    continue;
                }

                // Check for class/struct declaration: [access]? [kind-modifier]? (class|struct) Name ...
                if (TryParseTypeDeclaration(tokens, i, namespaceStack, out var decl, out var nextIndex))
                {
                    if (decl is not null)
                    {
                        declarations.Add(decl);
                    }
                    i = nextIndex;
                    continue;
                }
            }

            i++;
        }

        return declarations;
    }

    private static bool TryParseTypeDeclaration(
        IReadOnlyList<CppToken> tokens,
        int start,
        Stack<List<string>> namespaceStack,
        out CppDeclaration? declaration,
        out int nextIndex)
    {
        declaration = null;
        nextIndex = start + 1;

        var i = start;

        // Optional access specifier: public / private / protected / internal
        if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Identifier && AccessSpecifiers.Contains(tokens[i].Text))
        {
            i++;
        }

        // Optional kind modifier: ref / value / interface / enum
        var kindModifier = string.Empty;
        if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Identifier && KindModifiers.Contains(tokens[i].Text))
        {
            kindModifier = tokens[i].Text;
            i++;
        }

        // Must be 'class' or 'struct' (or plain 'enum' when we saw 'enum class' style handled above)
        if (i >= tokens.Count || tokens[i].Kind != CppTokenKind.Identifier)
        {
            return false;
        }

        var kindNoun = tokens[i].Text;
        var isEnumClass = kindModifier == "enum" && (kindNoun == "class" || kindNoun == "struct");
        var isPlainEnum = kindModifier == string.Empty && kindNoun == "enum";
        var isRefOrValue = (kindModifier is "ref" or "value") && (kindNoun == "class" || kindNoun == "struct");
        var isInterface = kindModifier == "interface" && (kindNoun == "class" || kindNoun == "struct");

        if (!(isEnumClass || isPlainEnum || isRefOrValue || isInterface))
        {
            return false;
        }
        i++;

        // Optional attribute after 'class' (e.g., class __declspec(dllexport) Foo)
        while (i < tokens.Count && tokens[i].Kind == CppTokenKind.Identifier && tokens[i].Text.StartsWith("__", StringComparison.Ordinal))
        {
            i++;
            if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "(")
            {
                i = SkipBalanced(tokens, i, "(", ")");
            }
        }

        // Identifier — class name
        if (i >= tokens.Count || tokens[i].Kind != CppTokenKind.Identifier)
        {
            return false;
        }
        var nameToken = tokens[i];
        var name = nameToken.Text;
        i++;

        // Optional 'sealed' / 'abstract' modifier (C++/CLI)
        var isAbstract = false;
        var isSealed = false;
        while (i < tokens.Count && tokens[i].Kind == CppTokenKind.Identifier
               && tokens[i].Text is "sealed" or "abstract")
        {
            if (tokens[i].Text == "abstract") isAbstract = true;
            else if (tokens[i].Text == "sealed") isSealed = true;
            i++;
        }

        // Now expect either ';' (forward decl), ':' (base list), or '{' (body)
        if (i >= tokens.Count)
        {
            return false;
        }

        var baseNames = new List<string>();
        var interfaceNames = new List<string>();

        if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ";")
        {
            // Forward declaration — ignore
            nextIndex = i + 1;
            return true;
        }

        if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ":")
        {
            i++;
            i = ParseBaseList(tokens, i, isInterface, baseNames, interfaceNames);
        }

        if (i >= tokens.Count || tokens[i].Kind != CppTokenKind.Punctuation || tokens[i].Text != "{")
        {
            // Not a real declaration we recognise — bail
            nextIndex = i;
            return false;
        }

        var kind = kindNoun switch
        {
            _ when isInterface => TypeKind.Interface,
            _ when isEnumClass || isPlainEnum => TypeKind.Enum,
            _ when kindModifier == "value" => TypeKind.Struct,
            _ => TypeKind.Class,
        };

        // Parse the body — extract members and consume the matching brace.
        var defaultAccess = DefaultAccessFor(kind, kindNoun, isInterface);
        var members = new List<CppMember>();
        if (kind == TypeKind.Enum)
        {
            nextIndex = ParseEnumBody(tokens, i + 1, members);
        }
        else
        {
            nextIndex = ParseClassBody(tokens, i + 1, name, defaultAccess, members);
        }

        var ns = FlattenNamespace(namespaceStack);
        declaration = new CppDeclaration(
            name, ns, kind, baseNames, interfaceNames, members,
            NameSpan: SpanOf(nameToken),
            IsAbstract: isAbstract,
            IsSealed: isSealed);
        return true;
    }

    private static CppSpan SpanOf(CppToken t) => new(t.Position, t.Length, t.Line);

    private static MemberAccessibility DefaultAccessFor(TypeKind kind, string kindNoun, bool isInterface)
    {
        if (isInterface || kind == TypeKind.Enum) return MemberAccessibility.Public;
        return kindNoun == "struct" ? MemberAccessibility.Public : MemberAccessibility.Private;
    }

    private static int ParseEnumBody(IReadOnlyList<CppToken> tokens, int start, List<CppMember> members)
    {
        var i = start;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "}")
            {
                return i + 1;
            }

            if (t.Kind == CppTokenKind.Identifier)
            {
                var name = t.Text;
                members.Add(new CppMember(
                    name, MemberKind.Field, MemberAccessibility.Public,
                    ReturnTypeDisplay: string.Empty, IsStatic: true,
                    NameSpan: SpanOf(t)));
                i++;
                // Optional "= expr"
                if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "=")
                {
                    while (i < tokens.Count
                           && !(tokens[i].Kind == CppTokenKind.Punctuation && (tokens[i].Text == "," || tokens[i].Text == "}")))
                    {
                        i++;
                    }
                }
                if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ",")
                {
                    i++;
                }
                continue;
            }

            i++;
        }
        return i;
    }

    private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
    {
        "static", "virtual", "override", "sealed", "abstract",
        "const", "readonly", "explicit", "implicit",
        "inline", "extern", "mutable", "volatile",
        "constexpr", "consteval", "friend", "new",
    };

    private static int ParseClassBody(
        IReadOnlyList<CppToken> tokens,
        int start,
        string enclosingTypeName,
        MemberAccessibility defaultAccess,
        List<CppMember> members)
    {
        var currentAccess = defaultAccess;
        var i = start;

        while (i < tokens.Count)
        {
            var t = tokens[i];

            if (t.Kind == CppTokenKind.Punctuation && t.Text == "}")
            {
                return i + 1;
            }

            // Attribute [ ... ] / [[ ... ]]
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "[")
            {
                i = SkipBalanced(tokens, i, "[", "]");
                continue;
            }

            // Nested template — skip
            if (t.Kind == CppTokenKind.Identifier && t.Text == "template")
            {
                i = SkipTemplate(tokens, i + 1);
                continue;
            }

            // Access specifier label: public : / private : / ...
            if (t.Kind == CppTokenKind.Identifier && AccessSpecifiers.Contains(t.Text)
                && i + 1 < tokens.Count && tokens[i + 1].Kind == CppTokenKind.Punctuation && tokens[i + 1].Text == ":")
            {
                currentAccess = ParseAccessibility(t.Text);
                i += 2;
                continue;
            }

            // Stand-alone semicolon
            if (t.Kind == CppTokenKind.Punctuation && t.Text == ";")
            {
                i++;
                continue;
            }

            // Try to parse one member declaration
            if (TryParseMember(tokens, i, enclosingTypeName, currentAccess, out var member, out var next))
            {
                if (member is not null)
                {
                    members.Add(member);
                }
                i = next;
                continue;
            }

            // If we can't recognise, skip a token — but if we hit '{' we must skip the block
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "{")
            {
                i = SkipBalanced(tokens, i, "{", "}");
                continue;
            }
            i++;
        }
        return i;
    }

    private static MemberAccessibility ParseAccessibility(string keyword) => keyword switch
    {
        "public" => MemberAccessibility.Public,
        "private" => MemberAccessibility.Private,
        "protected" => MemberAccessibility.Protected,
        "internal" => MemberAccessibility.Internal,
        _ => MemberAccessibility.Public,
    };

    private static bool TryParseMember(
        IReadOnlyList<CppToken> tokens,
        int start,
        string enclosingTypeName,
        MemberAccessibility access,
        out CppMember? member,
        out int nextIndex)
    {
        member = null;
        nextIndex = start;
        var i = start;

        var isStatic = false;
        while (i < tokens.Count && tokens[i].Kind == CppTokenKind.Identifier && MemberModifiers.Contains(tokens[i].Text))
        {
            if (tokens[i].Text == "static") isStatic = true;
            i++;
        }

        if (i >= tokens.Count)
        {
            return false;
        }

        var t = tokens[i];

        // property Type Name (block or auto)
        if (t.Kind == CppTokenKind.Identifier && t.Text == "property")
        {
            return TryParseProperty(tokens, i + 1, access, isStatic, out member, out nextIndex);
        }

        // event Type Name;
        if (t.Kind == CppTokenKind.Identifier && t.Text == "event")
        {
            return TryParseEvent(tokens, i + 1, access, isStatic, out member, out nextIndex);
        }

        // Destructor ~Name / Finalizer !Name — skip
        if (t.Kind == CppTokenKind.Punctuation && (t.Text == "~" || t.Text == "!")
            && i + 1 < tokens.Count && tokens[i + 1].Kind == CppTokenKind.Identifier && tokens[i + 1].Text == enclosingTypeName)
        {
            nextIndex = SkipToStatementEnd(tokens, i + 2);
            return true;
        }

        // Nested class declaration
        if (TryParseNestedTypeSkip(tokens, i, out var nestedNext))
        {
            nextIndex = nestedNext;
            return true;
        }

        // From here we expect a member starting with an optional type expression + name.
        // Collect tokens up to the significant punctuation to decide.
        return TryParseTypedMember(tokens, i, enclosingTypeName, access, isStatic, out member, out nextIndex);
    }

    private static bool TryParseTypedMember(
        IReadOnlyList<CppToken> tokens,
        int start,
        string enclosingTypeName,
        MemberAccessibility access,
        bool isStatic,
        out CppMember? member,
        out int nextIndex)
    {
        member = null;
        nextIndex = start;

        // Walk forward collecting a "token run" up to ; = { (
        var i = start;
        var runStart = i;
        var lastIdentifierIndex = -1;
        var sawParenOpen = false;
        var sawEquals = false;
        var sawBraceOpen = false;
        var sawSemicolon = false;
        var sawColon = false;  // constructor initializer list

        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation)
            {
                switch (t.Text)
                {
                    case "(":
                        sawParenOpen = true;
                        goto endLoop;
                    case "=":
                        sawEquals = true;
                        goto endLoop;
                    case "{":
                        sawBraceOpen = true;
                        goto endLoop;
                    case ";":
                        sawSemicolon = true;
                        goto endLoop;
                    case ":":
                        sawColon = true;
                        goto endLoop;
                    case "<":
                        // template arg list within type — skip as balanced
                        i = SkipBalanced(tokens, i, "<", ">");
                        continue;
                }
            }
            else if (t.Kind == CppTokenKind.Identifier)
            {
                lastIdentifierIndex = i;
            }
            i++;
        }
        endLoop:

        if (lastIdentifierIndex < 0)
        {
            nextIndex = i + 1;
            return true; // consumed but no member emitted
        }

        var memberName = tokens[lastIdentifierIndex].Text;
        var typeTokens = new List<string>();
        for (var k = runStart; k < lastIdentifierIndex; k++)
        {
            typeTokens.Add(tokens[k].Text);
        }
        var typeDisplay = string.Join(" ", typeTokens).Trim();

        var nameSpan = SpanOf(tokens[lastIdentifierIndex]);

        if (sawParenOpen)
        {
            // Method or constructor
            var isConstructor = memberName == enclosingTypeName && typeDisplay.Length == 0;
            var parameters = ParseParameterList(tokens, i);
            var afterParens = SkipBalanced(tokens, i, "(", ")");
            afterParens = SkipMethodDeclarationTail(tokens, afterParens);
            nextIndex = afterParens;

            var kind = isConstructor ? MemberKind.Constructor : MemberKind.Method;
            member = new CppMember(memberName, kind, access, typeDisplay, isStatic, parameters, nameSpan);
            return true;
        }

        if (sawSemicolon || sawEquals || sawBraceOpen || sawColon)
        {
            // Field
            var afterField = SkipUntilStatementBoundary(tokens, i);
            nextIndex = afterField;
            if (typeDisplay.Length == 0)
            {
                // Couldn't identify a type — treat as unknown to keep parser progressing
                return true;
            }
            member = new CppMember(memberName, MemberKind.Field, access, typeDisplay, isStatic, NameSpan: nameSpan);
            return true;
        }

        // Fallback — advance one
        nextIndex = start + 1;
        return true;
    }

    private static bool TryParseNestedTypeSkip(IReadOnlyList<CppToken> tokens, int start, out int nextIndex)
    {
        nextIndex = start;
        var i = start;
        var hasAccess = i < tokens.Count && tokens[i].Kind == CppTokenKind.Identifier && AccessSpecifiers.Contains(tokens[i].Text);
        var probe = hasAccess ? i + 1 : i;
        var hasKindMod = probe < tokens.Count && tokens[probe].Kind == CppTokenKind.Identifier && KindModifiers.Contains(tokens[probe].Text);
        var kindNounIdx = hasKindMod ? probe + 1 : probe;
        if (kindNounIdx >= tokens.Count) return false;

        // Nested "class Foo" or "struct Foo" (plain, without ref/value) is possible in C++/CLI too.
        if (tokens[kindNounIdx].Kind != CppTokenKind.Identifier) return false;
        if (tokens[kindNounIdx].Text is not ("class" or "struct" or "enum")) return false;

        if (!hasKindMod && tokens[kindNounIdx].Text is not "enum")
        {
            // Plain "class Foo" nested — skip the whole thing.
        }
        else if (!hasKindMod && tokens[kindNounIdx].Text == "enum")
        {
            // plain enum
        }
        // Skip from kindNounIdx until matching brace or ';'
        var j = kindNounIdx + 1;
        while (j < tokens.Count)
        {
            if (tokens[j].Kind == CppTokenKind.Punctuation && tokens[j].Text == "{")
            {
                j = SkipBalanced(tokens, j, "{", "}");
                // possible trailing ';'
                while (j < tokens.Count && tokens[j].Kind == CppTokenKind.Punctuation && tokens[j].Text == ";") j++;
                nextIndex = j;
                return true;
            }
            if (tokens[j].Kind == CppTokenKind.Punctuation && tokens[j].Text == ";")
            {
                nextIndex = j + 1;
                return true;
            }
            j++;
        }
        nextIndex = j;
        return true;
    }

    private static bool TryParseProperty(
        IReadOnlyList<CppToken> tokens,
        int start,
        MemberAccessibility access,
        bool isStatic,
        out CppMember? member,
        out int nextIndex)
    {
        member = null;
        nextIndex = start;

        // Collect tokens until ; or { — last identifier is the name.
        var i = start;
        var lastId = -1;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && (t.Text == ";" || t.Text == "{" || t.Text == "["))
            {
                break;
            }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "<")
            {
                i = SkipBalanced(tokens, i, "<", ">");
                continue;
            }
            if (t.Kind == CppTokenKind.Identifier)
            {
                lastId = i;
            }
            i++;
        }

        if (lastId < 0)
        {
            nextIndex = i + 1;
            return true;
        }

        var name = tokens[lastId].Text;
        var typeTokens = new List<string>();
        for (var k = start; k < lastId; k++)
        {
            typeTokens.Add(tokens[k].Text);
        }
        var typeDisplay = string.Join(" ", typeTokens).Trim();

        // If block form, skip the accessors block
        if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "{")
        {
            i = SkipBalanced(tokens, i, "{", "}");
            // optional trailing ;
            if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ";") i++;
        }
        else if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ";")
        {
            i++;
        }

        member = new CppMember(
            name, MemberKind.Property, access, typeDisplay, isStatic,
            NameSpan: SpanOf(tokens[lastId]));
        nextIndex = i;
        return true;
    }

    private static bool TryParseEvent(
        IReadOnlyList<CppToken> tokens,
        int start,
        MemberAccessibility access,
        bool isStatic,
        out CppMember? member,
        out int nextIndex)
    {
        member = null;
        var i = start;
        var lastId = -1;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && (t.Text == ";" || t.Text == "{"))
            {
                break;
            }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "<")
            {
                i = SkipBalanced(tokens, i, "<", ">");
                continue;
            }
            if (t.Kind == CppTokenKind.Identifier)
            {
                lastId = i;
            }
            i++;
        }

        if (lastId < 0)
        {
            nextIndex = i + 1;
            return true;
        }

        var name = tokens[lastId].Text;
        var typeTokens = new List<string>();
        for (var k = start; k < lastId; k++) typeTokens.Add(tokens[k].Text);
        var typeDisplay = string.Join(" ", typeTokens).Trim();

        if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "{")
        {
            i = SkipBalanced(tokens, i, "{", "}");
            if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ";") i++;
        }
        else if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ";")
        {
            i++;
        }

        member = new CppMember(
            name, MemberKind.Event, access, typeDisplay, isStatic,
            NameSpan: SpanOf(tokens[lastId]));
        nextIndex = i;
        return true;
    }

    private static IReadOnlyList<CppParameter> ParseParameterList(IReadOnlyList<CppToken> tokens, int openParenIndex)
    {
        var result = new List<CppParameter>();
        var closeParen = SkipBalanced(tokens, openParenIndex, "(", ")") - 1;
        var i = openParenIndex + 1;

        while (i < closeParen)
        {
            var start = i;

            // Walk until top-level comma or '=' (default value) inside this parameter slot.
            while (i < closeParen)
            {
                var t = tokens[i];
                if (t.Kind == CppTokenKind.Punctuation && t.Text == "<")
                {
                    i = SkipBalanced(tokens, i, "<", ">");
                    continue;
                }
                if (t.Kind == CppTokenKind.Punctuation && t.Text == "(")
                {
                    i = SkipBalanced(tokens, i, "(", ")");
                    continue;
                }
                if (t.Kind == CppTokenKind.Punctuation && (t.Text == "," || t.Text == "="))
                {
                    break;
                }
                i++;
            }

            // Extract param from tokens[start..i]
            var lastId = -1;
            for (var k = start; k < i; k++)
            {
                if (tokens[k].Kind == CppTokenKind.Identifier) lastId = k;
            }
            if (lastId > start)
            {
                var name = tokens[lastId].Text;
                var typeToks = new List<string>();
                for (var k = start; k < lastId; k++) typeToks.Add(tokens[k].Text);
                var type = string.Join(" ", typeToks).Trim();
                if (!string.IsNullOrEmpty(type))
                {
                    result.Add(new CppParameter(type, name));
                }
            }

            // Skip default-value expression if present.
            if (i < closeParen && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "=")
            {
                while (i < closeParen && !(tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ","))
                {
                    if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "(")
                    {
                        i = SkipBalanced(tokens, i, "(", ")");
                        continue;
                    }
                    i++;
                }
            }

            if (i < closeParen && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ",")
            {
                i++;
            }
        }
        return result;
    }

    private static int SkipToStatementEnd(IReadOnlyList<CppToken> tokens, int start)
    {
        var i = start;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "(") { i = SkipBalanced(tokens, i, "(", ")"); continue; }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "{") { i = SkipBalanced(tokens, i, "{", "}"); continue; }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == ";") return i + 1;
            i++;
        }
        return i;
    }

    /// <summary>
    /// Advance past a method declaration/definition tail. Accepts either a trailing `;`
    /// (declaration only) or an inline `{ ... }` body (definition). Nested parens are
    /// skipped so trailing qualifier annotations (e.g. `= 0`, `noexcept(...)`) don't trip us.
    /// </summary>
    private static int SkipMethodDeclarationTail(IReadOnlyList<CppToken> tokens, int start)
    {
        var i = start;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "(") { i = SkipBalanced(tokens, i, "(", ")"); continue; }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "{")
            {
                return SkipBalanced(tokens, i, "{", "}");
            }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == ";") return i + 1;
            i++;
        }
        return i;
    }

    private static int SkipUntilStatementBoundary(IReadOnlyList<CppToken> tokens, int start)
    {
        var i = start;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "(") { i = SkipBalanced(tokens, i, "(", ")"); continue; }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "{") { i = SkipBalanced(tokens, i, "{", "}"); continue; }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == ";") return i + 1;
            i++;
        }
        return i;
    }

    private static int ParseBaseList(
        IReadOnlyList<CppToken> tokens,
        int start,
        bool isInterface,
        List<string> baseNames,
        List<string> interfaceNames)
    {
        var i = start;
        var isFirst = true;
        while (i < tokens.Count)
        {
            // Skip access specifier
            if (tokens[i].Kind == CppTokenKind.Identifier && AccessSpecifiers.Contains(tokens[i].Text))
            {
                i++;
                continue;
            }
            // Skip 'virtual'
            if (tokens[i].Kind == CppTokenKind.Identifier && tokens[i].Text == "virtual")
            {
                i++;
                continue;
            }

            if (tokens[i].Kind == CppTokenKind.Identifier)
            {
                var typeName = ReadQualifiedName(tokens, ref i);
                // Skip generic bracket if any: <...>
                if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "<")
                {
                    i = SkipBalanced(tokens, i, "<", ">");
                }

                if (isInterface || !isFirst)
                {
                    interfaceNames.Add(typeName);
                }
                else
                {
                    baseNames.Add(typeName);
                }
                isFirst = false;

                if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ",")
                {
                    i++;
                    continue;
                }
                break;
            }
            break;
        }
        return i;
    }

    private static string ReadQualifiedName(IReadOnlyList<CppToken> tokens, ref int i)
    {
        var parts = new List<string>();
        while (i < tokens.Count && tokens[i].Kind == CppTokenKind.Identifier)
        {
            parts.Add(tokens[i].Text);
            i++;
            if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "::")
            {
                i++;
                continue;
            }
            break;
        }
        return string.Join("::", parts);
    }

    private static int HandleNamespace(
        IReadOnlyList<CppToken> tokens,
        int start,
        Stack<List<string>> namespaceStack,
        Stack<(int Depth, int PushCount)> braceDepthAtNamespace,
        ref int braceDepth)
    {
        var i = start;
        var parts = new List<string>();

        while (i < tokens.Count && tokens[i].Kind == CppTokenKind.Identifier)
        {
            parts.Add(tokens[i].Text);
            i++;
            if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "::")
            {
                i++;
                continue;
            }
            break;
        }

        // namespace ns = other; (alias) — skip to ';'
        if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "=")
        {
            while (i < tokens.Count && !(tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == ";")) i++;
            return i + 1;
        }

        if (i >= tokens.Count || tokens[i].Kind != CppTokenKind.Punctuation || tokens[i].Text != "{")
        {
            return i;
        }

        // Consume '{' and push
        i++;
        braceDepth++;
        var pushCount = parts.Count;
        if (pushCount == 0)
        {
            parts.Add(string.Empty);
            pushCount = 1;
        }
        namespaceStack.Push(parts);
        braceDepthAtNamespace.Push((braceDepth - 1, pushCount));
        // We still need to leave brace bookkeeping accurate; note namespaceStack now holds parts as a list — flatten via all frames
        return i;
    }

    private static int SkipTemplate(IReadOnlyList<CppToken> tokens, int start)
    {
        var i = start;
        if (i < tokens.Count && tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "<")
        {
            i = SkipBalanced(tokens, i, "<", ">");
        }
        return i;
    }

    private static int SkipBalanced(IReadOnlyList<CppToken> tokens, int start, string open, string close)
    {
        var depth = 0;
        var i = start;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation && t.Text == open) depth++;
            else if (t.Kind == CppTokenKind.Punctuation && t.Text == close)
            {
                depth--;
                if (depth == 0) return i + 1;
            }
            i++;
        }
        return i;
    }

    private static string FlattenNamespace(Stack<List<string>> stack)
    {
        if (stack.Count == 0) return string.Empty;
        var all = new List<string>();
        foreach (var frame in stack.Reverse())
        {
            foreach (var p in frame)
            {
                if (!string.IsNullOrEmpty(p))
                {
                    all.Add(p);
                }
            }
        }
        return string.Join(".", all);
    }
}
