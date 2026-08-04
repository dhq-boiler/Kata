using System.ComponentModel;
using Kata.Core.Analysis;
using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using ModelContextProtocol.Server;

namespace Kata.Mcp;

[McpServerToolType]
public sealed class KataTools
{
    private readonly KataSession _session;
    private readonly AiTaskQueue _aiTasks;

    public KataTools(KataSession session, AiTaskQueue aiTasks)
    {
        _session = session;
        _aiTasks = aiTasks;
    }

    [McpServerTool(Name = "load_solution")]
    [Description("Load a C# solution (.slnx or .sln) into the Kata session. All subsequent tools operate on the loaded model.")]
    public async Task<object> LoadSolution(
        [Description("Absolute filesystem path to the .slnx or .sln file")] string path,
        CancellationToken cancellationToken)
    {
        var model = await _session.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return new
        {
            path = model.FilePath,
            projectCount = model.Projects.Count,
            projects = model.Projects.Select(p => new { p.Name, typeCount = p.Types.Count }).ToArray(),
        };
    }

    [McpServerTool(Name = "list_projects")]
    [Description("List all projects in the currently-loaded solution with their type counts. Auto-loads from the Kata.App session handshake if no solution is explicitly loaded.")]
    public async Task<object> ListProjects(CancellationToken cancellationToken)
    {
        var model = await _session.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return model.Projects.Select(p => new
        {
            p.Name,
            p.FilePath,
            p.LanguageId,
            typeCount = p.Types.Count,
        }).ToArray();
    }

    [McpServerTool(Name = "list_types")]
    [Description("List types across projects. Optional filter: projectName restricts to one project, namespacePrefix filters by fully-qualified namespace prefix. Auto-loads from the Kata.App session handshake if no solution is explicitly loaded.")]
    public async Task<object> ListTypes(
        CancellationToken cancellationToken,
        [Description("Optional project name filter")] string? projectName = null,
        [Description("Optional namespace prefix filter")] string? namespacePrefix = null)
    {
        var model = await _session.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var projects = string.IsNullOrEmpty(projectName)
            ? model.Projects
            : model.Projects.Where(p => p.Name == projectName).ToList();

        var results = new List<object>();
        foreach (var project in projects)
        {
            foreach (var type in project.Types)
            {
                if (!string.IsNullOrEmpty(namespacePrefix) &&
                    !type.Namespace.FullName.StartsWith(namespacePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new
                {
                    projectName = project.Name,
                    fullName = type.Ref.FullyQualifiedName,
                    type.Name,
                    @namespace = type.Namespace.FullName,
                    kind = type.Kind.ToString(),
                    accessibility = type.Accessibility.ToString(),
                    memberCount = type.Members.Count,
                    baseCount = type.BaseTypes.Count,
                    interfaceCount = type.ImplementedInterfaces.Count,
                    type.IsGhost,
                });
            }
        }

        return results;
    }

    [McpServerTool(Name = "get_type")]
    [Description("Fetch full details for a single type by its fully-qualified name (e.g. \"Kata.Core.Intents.RenameIntent\"). Auto-loads from the Kata.App session handshake if no solution is explicitly loaded.")]
    public async Task<object> GetType(
        [Description("Fully-qualified type name (namespace + type name, without global::)")] string fullName,
        CancellationToken cancellationToken)
    {
        var model = await _session.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        foreach (var project in model.Projects)
        {
            var found = project.Types.FirstOrDefault(t => t.Ref.FullyQualifiedName == fullName);
            if (found is not null)
            {
                return SerializeType(project.Name, found);
            }
        }

        throw new InvalidOperationException($"Type not found: {fullName}");
    }

    [McpServerTool(Name = "get_type_graph")]
    [Description("Return the entire type graph: all types with their base types and implemented interfaces. Suitable for building a class diagram. Auto-loads from the Kata.App session handshake if no solution is explicitly loaded.")]
    public async Task<object> GetTypeGraph(CancellationToken cancellationToken)
    {
        var model = await _session.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var nodes = model.Projects.SelectMany(p => p.Types.Select(t => new
        {
            projectName = p.Name,
            fullName = t.Ref.FullyQualifiedName,
            t.Name,
            @namespace = t.Namespace.FullName,
            kind = t.Kind.ToString(),
            accessibility = t.Accessibility.ToString(),
            members = t.Members.Select(m => new
            {
                m.Name,
                kind = m.Kind.ToString(),
                accessibility = m.Accessibility.ToString(),
                m.ReturnTypeDisplay,
                m.IsStatic,
                m.IsGhost,
            }).ToArray(),
            t.IsGhost,
        })).ToArray();

        var edges = new List<object>();
        foreach (var project in model.Projects)
        {
            foreach (var type in project.Types)
            {
                foreach (var baseType in type.BaseTypes)
                {
                    edges.Add(new
                    {
                        source = type.Ref.FullyQualifiedName,
                        target = baseType.FullyQualifiedName,
                        kind = "inheritance",
                    });
                }

                foreach (var iface in type.ImplementedInterfaces)
                {
                    edges.Add(new
                    {
                        source = type.Ref.FullyQualifiedName,
                        target = iface.FullyQualifiedName,
                        kind = "interface",
                    });
                }
            }
        }

        return new { nodes, edges };
    }

    [McpServerTool(Name = "propose_rename")]
    [Description("Propose renaming a type (or one of its members). Returns a pending change set ID and diffs. The change is NOT written to disk until you call apply_change_set.")]
    public async Task<object> ProposeRename(
        [Description("Fully-qualified type name to rename, or containing type of the member being renamed (e.g. \"Kata.Core.Intents.RenameIntent\").")] string typeFullName,
        [Description("New identifier name.")] string newName,
        CancellationToken cancellationToken,
        [Description("Optional member signature (as returned in MemberRef.Signature). Omit to rename the type itself.")] string? memberSignature = null,
        [Description("Optional rationale — why this rename is proposed. Stored on the intent.")] string? rationale = null)
    {
        var typeRef = new TypeRef(typeFullName);
        MemberRef? memberRef = string.IsNullOrEmpty(memberSignature)
            ? null
            : new MemberRef(typeRef, memberSignature);

        var intent = IntentFactory.Rename(typeRef, newName, IntentSource.Ai, rationale, memberRef);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = memberRef is null
            ? $"Rename {typeFullName} → {newName}"
            : $"Rename {typeFullName}.{memberSignature} → {newName}";

        return RegisterAndSerialize(kind: "rename", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_extract_interface")]
    [Description("Propose extracting an interface from a class. Members are specified by their signature strings (as returned by list_types/get_type). Returns a pending change set with the new interface source and the modified class file diff.")]
    public async Task<object> ProposeExtractInterface(
        [Description("Fully-qualified name of the class to extract from.")] string typeFullName,
        [Description("Signatures of the members to include in the interface (as returned in MemberRef.Signature).")] string[] memberSignatures,
        [Description("Name of the new interface (typically prefixed with I).")] string interfaceName,
        CancellationToken cancellationToken,
        [Description("Optional target namespace for the new interface. Defaults to the source class's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why this extraction is proposed.")] string? rationale = null)
    {
        var typeRef = new TypeRef(typeFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(typeRef, sig)).ToArray();

        var intent = IntentFactory.ExtractInterface(
            sourceType: typeRef,
            members: memberRefs,
            proposedInterfaceName: interfaceName,
            source: IntentSource.Ai,
            rationale: rationale,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace));

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Extract interface {interfaceName} from {typeFullName} ({memberRefs.Length} members)";
        return RegisterAndSerialize(kind: "extract_interface", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_extract_superclass")]
    [Description("Propose extracting an abstract superclass from a class. Members are moved up (kept in the source class only when the target language cannot cleanly cut them). Returns a pending change set with the new superclass source, the modified class file diff, and (for cpp-cli) the vcxproj update.")]
    public async Task<object> ProposeExtractSuperclass(
        [Description("Fully-qualified name of the class to extract from.")] string typeFullName,
        [Description("Signatures of the members to move up (as returned in MemberRef.Signature).")] string[] memberSignatures,
        [Description("Name of the new superclass.")] string superclassName,
        CancellationToken cancellationToken,
        [Description("Optional target namespace for the new superclass. Defaults to the source class's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why this extraction is proposed.")] string? rationale = null)
    {
        var typeRef = new TypeRef(typeFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(typeRef, sig)).ToArray();

        var intent = IntentFactory.ExtractSuperclass(
            sourceType: typeRef,
            members: memberRefs,
            proposedSuperclassName: superclassName,
            source: IntentSource.Ai,
            rationale: rationale,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace));

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Extract superclass {superclassName} from {typeFullName} ({memberRefs.Length} members)";
        return RegisterAndSerialize(kind: "extract_superclass", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_extract_class")]
    [Description("Propose extracting a class from another class (Fowler's Extract Class — also the mechanical action behind Fowler's Big Refactoring 'Separate Domain from Presentation'). Selected members are moved into a new non-abstract class, and the source class gains a delegate property pointing to it. Call sites are NOT rewritten — treat compiler errors as the follow-up work.")]
    public async Task<object> ProposeExtractClass(
        [Description("Fully-qualified name of the class to extract from.")] string typeFullName,
        [Description("Signatures of the members to move out into the new class (as returned in MemberRef.Signature).")] string[] memberSignatures,
        [Description("Name of the new class.")] string newClassName,
        [Description("Property name that the source class will use to reach the new object (e.g. \"Telephone\").")] string delegatePropertyName,
        CancellationToken cancellationToken,
        [Description("Optional target namespace for the new class. Defaults to the source class's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why this extraction is proposed.")] string? rationale = null)
    {
        var typeRef = new TypeRef(typeFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(typeRef, sig)).ToArray();

        var intent = IntentFactory.ExtractClass(
            sourceType: typeRef,
            members: memberRefs,
            proposedClassName: newClassName,
            delegatePropertyName: delegatePropertyName,
            source: IntentSource.Ai,
            rationale: rationale,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace));

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Extract class {newClassName} from {typeFullName} ({memberRefs.Length} members)";
        return RegisterAndSerialize(kind: "extract_class", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_collapse_hierarchy")]
    [Description("Propose collapsing a subclass into its base (Fowler's Collapse Hierarchy). Every member of the subclass is moved up onto the parent, the subclass source file is deleted, and every usage of the subclass name is rewritten to the parent name.")]
    public async Task<object> ProposeCollapseHierarchy(
        [Description("Fully-qualified name of the subclass to collapse.")] string subclassFullName,
        [Description("Fully-qualified name of the parent type that will absorb the subclass.")] string parentFullName,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why collapsing this hierarchy is appropriate.")] string? rationale = null)
    {
        var intent = IntentFactory.CollapseHierarchy(
            subclass: new TypeRef(subclassFullName),
            parent: new TypeRef(parentFullName),
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Collapse {subclassFullName} into {parentFullName}";
        return RegisterAndSerialize(kind: "collapse_hierarchy", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_remove_subclass")]
    [Description("Propose removing a subclass and rewriting every usage to fall back to its base type (Fowler's Remove Subclass). The subclass source file is deleted, and identifier occurrences of the subclass name across the project are replaced with the base class name via whole-word regex.")]
    public async Task<object> ProposeRemoveSubclass(
        [Description("Fully-qualified name of the subclass to remove.")] string subclassFullName,
        [Description("Fully-qualified name of the base type that will replace every usage of the subclass.")] string replacementBaseFullName,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why removing this subclass is appropriate.")] string? rationale = null)
    {
        var intent = IntentFactory.RemoveSubclass(
            subclass: new TypeRef(subclassFullName),
            replacementBase: new TypeRef(replacementBaseFullName),
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Remove subclass {subclassFullName} → {replacementBaseFullName}";
        return RegisterAndSerialize(kind: "remove_subclass", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_pull_up_method")]
    [Description("Propose Fowler's Pull Up Method: move one or more methods from a subclass up to its parent class. Members are specified by their signature strings as returned by list_types/get_type.")]
    public async Task<object> ProposePullUpMethod(
        [Description("Fully-qualified name of the subclass whose methods will be moved up.")] string subclassFullName,
        [Description("Fully-qualified name of the parent class that will receive the methods.")] string parentFullName,
        [Description("Signatures of the members to pull up (as returned in MemberRef.Signature).")] string[] memberSignatures,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the pull-up is proposed.")] string? rationale = null)
    {
        var subRef = new TypeRef(subclassFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(subRef, sig)).ToArray();

        var intent = IntentFactory.PullUpMethod(
            subclass: subRef,
            parent: new TypeRef(parentFullName),
            members: memberRefs,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Pull up {memberRefs.Length} member(s) from {subclassFullName} to {parentFullName}";
        return RegisterAndSerialize(kind: "pull_up_method", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_push_down_method")]
    [Description("Propose Fowler's Push Down Method: move one or more methods from a parent class down to a specific subclass. Members are specified by their signature strings as returned by list_types/get_type.")]
    public async Task<object> ProposePushDownMethod(
        [Description("Fully-qualified name of the parent class whose methods will be moved down.")] string parentFullName,
        [Description("Fully-qualified name of the subclass that will receive the methods.")] string subclassFullName,
        [Description("Signatures of the members to push down (as returned in MemberRef.Signature).")] string[] memberSignatures,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the push-down is proposed.")] string? rationale = null)
    {
        var parentRef = new TypeRef(parentFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(parentRef, sig)).ToArray();

        var intent = IntentFactory.PushDownMethod(
            parent: parentRef,
            subclass: new TypeRef(subclassFullName),
            members: memberRefs,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Push down {memberRefs.Length} member(s) from {parentFullName} to {subclassFullName}";
        return RegisterAndSerialize(kind: "push_down_method", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_pull_up_field")]
    [Description("Propose Fowler's Pull Up Field: move one or more fields from a subclass up to its parent class.")]
    public async Task<object> ProposePullUpField(
        [Description("Fully-qualified name of the subclass whose fields will be moved up.")] string subclassFullName,
        [Description("Fully-qualified name of the parent class that will receive the fields.")] string parentFullName,
        [Description("Signatures of the fields to pull up (as returned in MemberRef.Signature).")] string[] memberSignatures,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the pull-up is proposed.")] string? rationale = null)
    {
        var subRef = new TypeRef(subclassFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(subRef, sig)).ToArray();

        var intent = IntentFactory.PullUpField(
            subclass: subRef,
            parent: new TypeRef(parentFullName),
            members: memberRefs,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Pull up {memberRefs.Length} field(s) from {subclassFullName} to {parentFullName}";
        return RegisterAndSerialize(kind: "pull_up_field", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_push_down_field")]
    [Description("Propose Fowler's Push Down Field: move one or more fields from a parent class down to a specific subclass.")]
    public async Task<object> ProposePushDownField(
        [Description("Fully-qualified name of the parent class whose fields will be moved down.")] string parentFullName,
        [Description("Fully-qualified name of the subclass that will receive the fields.")] string subclassFullName,
        [Description("Signatures of the fields to push down (as returned in MemberRef.Signature).")] string[] memberSignatures,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the push-down is proposed.")] string? rationale = null)
    {
        var parentRef = new TypeRef(parentFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(parentRef, sig)).ToArray();

        var intent = IntentFactory.PushDownField(
            parent: parentRef,
            subclass: new TypeRef(subclassFullName),
            members: memberRefs,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Push down {memberRefs.Length} field(s) from {parentFullName} to {subclassFullName}";
        return RegisterAndSerialize(kind: "push_down_field", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_remove_setting_method")]
    [Description("Propose Fowler's Remove Setting Method: drop the setter of a property (or a matching setter-prefixed method) to make it read-only. For C++/CLI targets, a method named 'Set<PropertyName>' is removed.")]
    public async Task<object> ProposeRemoveSettingMethod(
        [Description("Fully-qualified name of the class that owns the property.")] string ownerFullName,
        [Description("Property name or signature whose setter should be removed (as returned in MemberRef.Signature).")] string propertySignature,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why removing the setter is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.RemoveSettingMethod(
            ownerType: ownerRef,
            property: new MemberRef(ownerRef, propertySignature),
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Remove setter from {ownerFullName}.{propertySignature}";
        return RegisterAndSerialize(kind: "remove_setting_method", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_rename_field")]
    [Description("Propose Fowler's Rename Field: rename a field on a specific class. Behaves like propose_rename with a member signature but is exposed as a distinct tool for the Fowler catalog.")]
    public async Task<object> ProposeRenameField(
        [Description("Fully-qualified name of the class that owns the field.")] string ownerFullName,
        [Description("Signature of the field to rename (as returned in MemberRef.Signature).")] string fieldSignature,
        [Description("New identifier for the field.")] string newName,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the rename is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.RenameField(
            ownerType: ownerRef,
            field: new MemberRef(ownerRef, fieldSignature),
            newName: newName,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Rename field {ownerFullName}.{fieldSignature} → {newName}";
        return RegisterAndSerialize(kind: "rename_field", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_pull_up_constructor_body")]
    [Description("Propose Fowler's Pull Up Constructor Body: move the first constructor's body from a subclass up into its parent's constructor (creating the parent constructor if missing). The subclass constructor becomes an empty block that delegates to the parent via `: base()`.")]
    public async Task<object> ProposePullUpConstructorBody(
        [Description("Fully-qualified name of the subclass whose constructor body will be moved up.")] string subclassFullName,
        [Description("Fully-qualified name of the parent class that will absorb the constructor body.")] string parentFullName,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the pull-up is proposed.")] string? rationale = null)
    {
        var intent = IntentFactory.PullUpConstructorBody(
            subclass: new TypeRef(subclassFullName),
            parent: new TypeRef(parentFullName),
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Pull up constructor body from {subclassFullName} to {parentFullName}";
        return RegisterAndSerialize(kind: "pull_up_constructor_body", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_encapsulate_field")]
    [Description("Propose Fowler's Encapsulate Field: convert a public field into an auto-property of the same name and access level. Existing read/write call sites remain valid syntactically.")]
    public async Task<object> ProposeEncapsulateField(
        [Description("Fully-qualified name of the class that owns the field.")] string ownerFullName,
        [Description("Signature of the field to encapsulate (as returned in MemberRef.Signature).")] string fieldSignature,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the encapsulation is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.EncapsulateField(
            ownerType: ownerRef,
            field: new MemberRef(ownerRef, fieldSignature),
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Encapsulate field {ownerFullName}.{fieldSignature}";
        return RegisterAndSerialize(kind: "encapsulate_field", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_move_method")]
    [Description("Propose Fowler's Move Method: transfer one or more methods from one class to another. Unlike propose_pull_up_method, source and target do NOT need to be related by inheritance.")]
    public async Task<object> ProposeMoveMethod(
        [Description("Fully-qualified name of the source class that currently owns the methods.")] string sourceFullName,
        [Description("Fully-qualified name of the target class that will receive the methods.")] string targetFullName,
        [Description("Signatures of the methods to move (as returned in MemberRef.Signature).")] string[] memberSignatures,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the move is proposed.")] string? rationale = null)
    {
        var srcRef = new TypeRef(sourceFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(srcRef, sig)).ToArray();

        var intent = IntentFactory.MoveMethod(
            sourceType: srcRef,
            targetType: new TypeRef(targetFullName),
            members: memberRefs,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Move {memberRefs.Length} method(s) from {sourceFullName} to {targetFullName}";
        return RegisterAndSerialize(kind: "move_method", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_move_field")]
    [Description("Propose Fowler's Move Field: transfer one or more fields from one class to another. Source and target do NOT need to be related by inheritance.")]
    public async Task<object> ProposeMoveField(
        [Description("Fully-qualified name of the source class that currently owns the fields.")] string sourceFullName,
        [Description("Fully-qualified name of the target class that will receive the fields.")] string targetFullName,
        [Description("Signatures of the fields to move (as returned in MemberRef.Signature).")] string[] memberSignatures,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the move is proposed.")] string? rationale = null)
    {
        var srcRef = new TypeRef(sourceFullName);
        var memberRefs = memberSignatures.Select(sig => new MemberRef(srcRef, sig)).ToArray();

        var intent = IntentFactory.MoveField(
            sourceType: srcRef,
            targetType: new TypeRef(targetFullName),
            members: memberRefs,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Move {memberRefs.Length} field(s) from {sourceFullName} to {targetFullName}";
        return RegisterAndSerialize(kind: "move_field", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_replace_constructor_with_factory")]
    [Description("Propose Fowler's Replace Constructor with Factory Method: add a public static Create factory that news up the type, and (by default) make the original constructor private.")]
    public async Task<object> ProposeReplaceConstructorWithFactory(
        [Description("Fully-qualified name of the class whose constructor should be replaced.")] string ownerFullName,
        CancellationToken cancellationToken,
        [Description("Name of the new factory method (default: 'Create').")] string factoryName = "Create",
        [Description("Whether to make the original constructor private (default: true).")] bool makeConstructorPrivate = true,
        [Description("Optional rationale — why this refactor is proposed.")] string? rationale = null)
    {
        var intent = IntentFactory.ReplaceConstructorWithFactory(
            ownerType: new TypeRef(ownerFullName),
            source: IntentSource.Ai,
            factoryName: factoryName,
            makeConstructorPrivate: makeConstructorPrivate,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Replace constructor with factory on {ownerFullName} ({factoryName})";
        return RegisterAndSerialize(kind: "replace_constructor_with_factory", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_replace_magic_number")]
    [Description("Propose Fowler's Replace Magic Number with Symbolic Constant: introduce a private const named ConstantName with the given LiteralValue on OwnerType, and replace every literal occurrence of LiteralValue within that class body.")]
    public async Task<object> ProposeReplaceMagicNumber(
        [Description("Fully-qualified name of the class that holds the magic number.")] string ownerFullName,
        [Description("Literal value as it appears in source (e.g. '3.14159', '100', '0.1m').")] string literalValue,
        [Description("Name of the new constant.")] string constantName,
        CancellationToken cancellationToken,
        [Description("C# type of the constant (default: 'int').")] string constantType = "int",
        [Description("Optional rationale — why this refactor is proposed.")] string? rationale = null)
    {
        var intent = IntentFactory.ReplaceMagicNumber(
            ownerType: new TypeRef(ownerFullName),
            literalValue: literalValue,
            constantName: constantName,
            source: IntentSource.Ai,
            constantType: constantType,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Replace {literalValue} → {constantName} on {ownerFullName}";
        return RegisterAndSerialize(kind: "replace_magic_number", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_change_bidirectional_to_unidirectional")]
    [Description("Propose Fowler's Change Bidirectional Association to Unidirectional: remove a field (or auto-property) from OwnerType that back-references some other class, keeping only the other direction of the association.")]
    public async Task<object> ProposeChangeBidirectionalToUnidirectional(
        [Description("Fully-qualified name of the class that owns the back-reference field.")] string ownerFullName,
        [Description("Signature of the field to drop (as returned in MemberRef.Signature).")] string fieldSignature,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why removing this direction is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.ChangeBidirectionalToUnidirectional(
            ownerType: ownerRef,
            field: new MemberRef(ownerRef, fieldSignature),
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Drop back-reference {ownerFullName}.{fieldSignature}";
        return RegisterAndSerialize(kind: "change_bidirectional_to_unidirectional", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_introduce_parameter_object")]
    [Description("Propose Fowler's Introduce Parameter Object: bundle a method's parameters into a new class. The method signature becomes single-parameter, references inside the method body are rewritten to `parameterName.originalName`, and a new class file is added. Call sites are NOT rewritten — treat the compiler errors as follow-up work.")]
    public async Task<object> ProposeIntroduceParameterObject(
        [Description("Fully-qualified name of the class that owns the method.")] string ownerFullName,
        [Description("Signature of the method to refactor (as returned in MemberRef.Signature).")] string methodSignature,
        [Description("Name of the new parameter-object class.")] string proposedObjectName,
        CancellationToken cancellationToken,
        [Description("Name of the single parameter that will hold the object inside the method (default 'args').")] string parameterName = "args",
        [Description("Optional target namespace for the new class. Defaults to the owner's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why the bundling is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.IntroduceParameterObject(
            ownerType: ownerRef,
            method: new MemberRef(ownerRef, methodSignature),
            proposedObjectName: proposedObjectName,
            source: IntentSource.Ai,
            parameterName: parameterName,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace),
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Introduce parameter object {proposedObjectName} on {ownerFullName}.{methodSignature}";
        return RegisterAndSerialize(kind: "introduce_parameter_object", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_add_parameter")]
    [Description("Propose Fowler's Add Parameter: append a new parameter to a method signature. Call sites are NOT rewritten; provide a defaultValue if you want call sites to keep compiling.")]
    public async Task<object> ProposeAddParameter(
        [Description("Fully-qualified name of the class that owns the method.")] string ownerFullName,
        [Description("Signature of the method to modify (as returned in MemberRef.Signature).")] string methodSignature,
        [Description("C# type of the new parameter (e.g. 'int', 'string', 'System.DateTime').")] string parameterType,
        [Description("Identifier for the new parameter.")] string parameterName,
        CancellationToken cancellationToken,
        [Description("Optional default value expression (e.g. '0', '\"\"', 'null').")] string? defaultValue = null,
        [Description("Optional rationale — why the parameter is added.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.AddParameter(
            ownerType: ownerRef,
            method: new MemberRef(ownerRef, methodSignature),
            parameterType: parameterType,
            parameterName: parameterName,
            source: IntentSource.Ai,
            defaultValue: defaultValue,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Add parameter {parameterType} {parameterName} to {ownerFullName}.{methodSignature}";
        return RegisterAndSerialize(kind: "add_parameter", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_remove_parameter")]
    [Description("Propose Fowler's Remove Parameter: drop a named parameter from a method signature. Call sites and the method body are NOT rewritten — the compiler will surface the follow-up work.")]
    public async Task<object> ProposeRemoveParameter(
        [Description("Fully-qualified name of the class that owns the method.")] string ownerFullName,
        [Description("Signature of the method to modify (as returned in MemberRef.Signature).")] string methodSignature,
        [Description("Name of the parameter to remove.")] string parameterName,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the parameter is removed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.RemoveParameter(
            ownerType: ownerRef,
            method: new MemberRef(ownerRef, methodSignature),
            parameterName: parameterName,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Remove parameter {parameterName} from {ownerFullName}.{methodSignature}";
        return RegisterAndSerialize(kind: "remove_parameter", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_replace_data_value_with_object")]
    [Description("Propose Fowler's Replace Data Value with Object: wrap a primitive field (or property) in a new small class that holds it as an inner property. The owner's field type changes to the new class, and a new class file is added. Call sites are NOT rewritten.")]
    public async Task<object> ProposeReplaceDataValueWithObject(
        [Description("Fully-qualified name of the class that owns the field.")] string ownerFullName,
        [Description("Signature of the field/property to promote (as returned in MemberRef.Signature).")] string fieldSignature,
        [Description("Name of the new wrapper class.")] string wrapperClassName,
        CancellationToken cancellationToken,
        [Description("Name of the inner property/field inside the wrapper (default 'Value').")] string innerFieldName = "Value",
        [Description("Optional target namespace for the new class. Defaults to the owner's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why the promotion is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.ReplaceDataValueWithObject(
            ownerType: ownerRef,
            field: new MemberRef(ownerRef, fieldSignature),
            wrapperClassName: wrapperClassName,
            source: IntentSource.Ai,
            innerFieldName: innerFieldName,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace),
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Wrap {ownerFullName}.{fieldSignature} in class {wrapperClassName}";
        return RegisterAndSerialize(kind: "replace_data_value_with_object", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_rename_parameter")]
    [Description("Propose renaming a parameter on a method. Uses Roslyn's Renamer under the hood so declaration and body references are updated together.")]
    public async Task<object> ProposeRenameParameter(
        [Description("Fully-qualified name of the class that owns the method.")] string ownerFullName,
        [Description("Signature of the method (as returned in MemberRef.Signature).")] string methodSignature,
        [Description("Existing parameter name.")] string oldName,
        [Description("New parameter name.")] string newName,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the rename is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.RenameParameter(
            ownerType: ownerRef,
            method: new MemberRef(ownerRef, methodSignature),
            oldName: oldName,
            newName: newName,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Rename parameter {oldName} → {newName} on {ownerFullName}.{methodSignature}";
        return RegisterAndSerialize(kind: "rename_parameter", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_self_encapsulate_field")]
    [Description("Propose Fowler's Self Encapsulate Field: introduce a public property for a private field and rewrite the class's internal usages to go through the property instead of the field directly. The field itself is kept.")]
    public async Task<object> ProposeSelfEncapsulateField(
        [Description("Fully-qualified name of the class that owns the field.")] string ownerFullName,
        [Description("Signature of the field to encapsulate (as returned in MemberRef.Signature).")] string fieldSignature,
        CancellationToken cancellationToken,
        [Description("Optional name of the accessor property; defaults to PascalCase(fieldName).")] string? propertyName = null,
        [Description("Optional rationale — why the encapsulation is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.SelfEncapsulateField(
            ownerType: ownerRef,
            field: new MemberRef(ownerRef, fieldSignature),
            source: IntentSource.Ai,
            propertyName: propertyName,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Self-encapsulate field {ownerFullName}.{fieldSignature}";
        return RegisterAndSerialize(kind: "self_encapsulate_field", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_change_reference_to_value")]
    [Description("Propose Fowler's Change Reference to Value: make an entire class effectively immutable by adding 'readonly' to mutable fields and converting property setters into init-only accessors.")]
    public async Task<object> ProposeChangeReferenceToValue(
        [Description("Fully-qualified name of the class to lock down.")] string ownerFullName,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the conversion is proposed.")] string? rationale = null)
    {
        var intent = IntentFactory.ChangeReferenceToValue(
            ownerType: new TypeRef(ownerFullName),
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Lock down {ownerFullName} (readonly fields, init-only properties)";
        return RegisterAndSerialize(kind: "change_reference_to_value", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_change_value_to_reference")]
    [Description("Propose Fowler's Change Value to Reference: introduce a static Dictionary registry and a GetOrCreate factory onto the class so instances can be shared by a key instead of created fresh each time.")]
    public async Task<object> ProposeChangeValueToReference(
        [Description("Fully-qualified name of the class to convert to a shared-reference type.")] string ownerFullName,
        CancellationToken cancellationToken,
        [Description("C# type of the registry key (default 'string').")] string keyType = "string",
        [Description("Name of the static factory method (default 'GetOrCreate').")] string factoryName = "GetOrCreate",
        [Description("Name of the private registry field (default '_instances').")] string registryFieldName = "_instances",
        [Description("Optional rationale — why the conversion is proposed.")] string? rationale = null)
    {
        var intent = IntentFactory.ChangeValueToReference(
            ownerType: new TypeRef(ownerFullName),
            source: IntentSource.Ai,
            keyType: keyType,
            factoryName: factoryName,
            registryFieldName: registryFieldName,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Add shared-instance registry to {ownerFullName}";
        return RegisterAndSerialize(kind: "change_value_to_reference", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_replace_type_code_with_class")]
    [Description("Propose Fowler's Replace Type Code with Class: replace an int/string type-code field with a new class that has static readonly instances per code. The owner's field type changes to the new class, and a new class file is added. Assignment sites are NOT rewritten.")]
    public async Task<object> ProposeReplaceTypeCodeWithClass(
        [Description("Fully-qualified name of the class that owns the type-code field.")] string ownerFullName,
        [Description("Signature of the type-code field or property (as returned in MemberRef.Signature).")] string fieldSignature,
        [Description("Name of the new type-code class.")] string newClassName,
        [Description("List of (name, value) pairs for each code. Each entry is a single string in 'Name=Value' form (e.g. 'Male=0', 'FEMALE=1').")] string[] codeEntries,
        CancellationToken cancellationToken,
        [Description("C# type of the underlying code (default 'int').")] string innerCodeType = "int",
        [Description("Optional target namespace for the new class. Defaults to the owner's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why the conversion is proposed.")] string? rationale = null)
    {
        var codes = codeEntries.Select(e =>
        {
            var eq = e.IndexOf('=');
            if (eq < 0) throw new InvalidOperationException($"codeEntries entry must be 'Name=Value': got '{e}'");
            return new TypeCodeEntry(e[..eq].Trim(), e[(eq + 1)..].Trim());
        }).ToArray();

        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.ReplaceTypeCodeWithClass(
            ownerType: ownerRef,
            field: new MemberRef(ownerRef, fieldSignature),
            newClassName: newClassName,
            codes: codes,
            source: IntentSource.Ai,
            innerCodeType: innerCodeType,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace),
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Replace type code {ownerFullName}.{fieldSignature} with class {newClassName}";
        return RegisterAndSerialize(kind: "replace_type_code_with_class", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_preserve_whole_object")]
    [Description("Propose Fowler's Preserve Whole Object: replace a set of derived-value parameters on a method with a single whole-object parameter. Occurrences of the removed parameter names inside the method body are rewritten to `objectParam.originalName` (matching field/property assumed on the whole object).")]
    public async Task<object> ProposePreserveWholeObject(
        [Description("Fully-qualified name of the class that owns the method.")] string ownerFullName,
        [Description("Signature of the method to refactor (as returned in MemberRef.Signature).")] string methodSignature,
        [Description("Fully-qualified name of the whole-object type that will replace the parameters.")] string objectFullName,
        [Description("Name of the new single parameter that holds the whole object.")] string parameterName,
        [Description("Names of the existing parameters to fold into the whole object.")] string[] replacedParameterNames,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the bundling is proposed.")] string? rationale = null)
    {
        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.PreserveWholeObject(
            ownerType: ownerRef,
            method: new MemberRef(ownerRef, methodSignature),
            objectType: new TypeRef(objectFullName),
            parameterName: parameterName,
            replacedParameterNames: replacedParameterNames,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Preserve whole object on {ownerFullName}.{methodSignature} ({parameterName}: {objectFullName})";
        return RegisterAndSerialize(kind: "preserve_whole_object", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_replace_array_with_object")]
    [Description("Propose Fowler's Replace Array with Object: promote an array-typed field into a named class whose properties correspond to specific array indices. The owner's field type changes to the new class, and a new class file is added. Call sites (`arr[i]` accesses) are NOT rewritten.")]
    public async Task<object> ProposeReplaceArrayWithObject(
        [Description("Fully-qualified name of the class that owns the array field.")] string ownerFullName,
        [Description("Signature of the array field (as returned in MemberRef.Signature).")] string fieldSignature,
        [Description("Name of the new class.")] string newClassName,
        [Description("Index → field mappings. Each entry is a single string in 'index:fieldName:fieldType' form, e.g. '0:Name:string', '1:Age:int'.")] string[] fieldMappings,
        CancellationToken cancellationToken,
        [Description("Optional target namespace for the new class. Defaults to the owner's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why the promotion is proposed.")] string? rationale = null)
    {
        var parsed = fieldMappings.Select(m =>
        {
            var parts = m.Split(':', 3);
            if (parts.Length < 3) throw new InvalidOperationException($"fieldMappings entry must be 'index:fieldName:fieldType': got '{m}'");
            if (!int.TryParse(parts[0].Trim(), out var idx))
            {
                throw new InvalidOperationException($"fieldMappings entry has non-integer index: '{m}'");
            }
            return new ArrayFieldMapping(idx, parts[1].Trim(), parts[2].Trim());
        }).ToArray();

        var ownerRef = new TypeRef(ownerFullName);
        var intent = IntentFactory.ReplaceArrayWithObject(
            ownerType: ownerRef,
            arrayField: new MemberRef(ownerRef, fieldSignature),
            newClassName: newClassName,
            fieldMappings: parsed,
            source: IntentSource.Ai,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace),
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Replace array {ownerFullName}.{fieldSignature} with class {newClassName}";
        return RegisterAndSerialize(kind: "replace_array_with_object", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_replace_type_code_with_subclasses")]
    [Description("Propose Fowler's Replace Type Code with Subclasses: mark the owner class 'abstract' and create one empty subclass file per code. The user is expected to move type-code-specific behaviour into each subclass afterwards.")]
    public async Task<object> ProposeReplaceTypeCodeWithSubclasses(
        [Description("Fully-qualified name of the owner class that currently carries the type code.")] string ownerFullName,
        [Description("Names of the subclasses to create (one per code value).")] string[] subclassNames,
        CancellationToken cancellationToken,
        [Description("Optional target namespace for the new subclasses. Defaults to the owner's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why the subclassing is proposed.")] string? rationale = null)
    {
        var intent = IntentFactory.ReplaceTypeCodeWithSubclasses(
            ownerType: new TypeRef(ownerFullName),
            subclassNames: subclassNames,
            source: IntentSource.Ai,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace),
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Turn {ownerFullName} into abstract with subclasses {string.Join(", ", subclassNames)}";
        return RegisterAndSerialize(kind: "replace_type_code_with_subclasses", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_introduce_assertion")]
    [Description("Propose Fowler's Introduce Assertion: insert `System.Diagnostics.Debug.Assert(condition, \"message\");` at the top of the innermost block containing the given character offset. Typical use is asserting a precondition at method entry — plant the caret anywhere inside the method body.")]
    public async Task<object> ProposeIntroduceAssertion(
        [Description("Fully-qualified name of the containing class.")] string ownerFullName,
        [Description("Signature of the method that gets the assertion.")] string containingMemberSignature,
        [Description("Character offset (0-based) — assertion is inserted at the top of the block containing this offset.")] int selectionStart,
        [Description("Boolean expression that must be true (C# syntax).")] string assertionExpression,
        CancellationToken cancellationToken,
        [Description("Optional failure message. Defaults to the assertion expression's text.")] string? message = null,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.IntroduceAssertion(
            owner, new MemberRef(owner, containingMemberSignature),
            selectionStart, assertionExpression,
            IntentSource.Ai, message, rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        return RegisterAndSerialize(kind: "introduce_assertion",
            label: $"Introduce assertion `{assertionExpression}` in {ownerFullName}.{containingMemberSignature}",
            changeSet, intent);
    }

    [McpServerTool(Name = "propose_introduce_null_object")]
    [Description("Propose Fowler's Introduce Null Object: scaffold a `Null{SourceType}` subclass file with override stubs for every virtual/abstract instance method — void overrides are no-ops, value-returning overrides return default. Users then swap `null` for `NullType.Instance` at construction sites and drop the null checks manually. Refuses when SourceType is sealed (subclassing impossible).")]
    public async Task<object> ProposeIntroduceNullObject(
        [Description("Fully-qualified name of the class to introduce a Null Object for.")] string sourceTypeFullName,
        CancellationToken cancellationToken,
        [Description("Optional name for the null-object class. Defaults to `Null{SourceType}`.")] string? nullClassName = null,
        [Description("Optional target namespace for the new file. Defaults to the source type's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var intent = IntentFactory.IntroduceNullObject(
            sourceType: new TypeRef(sourceTypeFullName),
            source: IntentSource.Ai,
            nullClassName: nullClassName,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace),
            rationale: rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        return RegisterAndSerialize(kind: "introduce_null_object",
            label: $"Introduce Null Object for {sourceTypeFullName}",
            changeSet, intent);
    }

    [McpServerTool(Name = "propose_replace_nested_conditional_with_guard_clauses")]
    [Description("Propose Fowler's Replace Nested Conditional with Guard Clauses: convert an if-else where ONE branch is a single return/throw into a guard clause + un-indented body. If the then-branch is the guard, drops the else and hoists the else's contents. If the else-branch is the guard, inverts the condition and hoists the then's contents. Refuses when neither branch is a single return/throw.")]
    public async Task<object> ProposeReplaceNestedConditionalWithGuardClauses(
        [Description("Fully-qualified name of the containing class.")] string ownerFullName,
        [Description("Signature of the method containing the if-statement.")] string containingMemberSignature,
        [Description("Character offset (0-based) at the if-statement.")] int selectionStart,
        [Description("Length in characters (1 is fine — Kata locates the enclosing if).")] int selectionLength,
        CancellationToken cancellationToken,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.ReplaceNestedConditionalWithGuardClauses(
            owner, new MemberRef(owner, containingMemberSignature),
            selectionStart, System.Math.Max(1, selectionLength),
            IntentSource.Ai, rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        return RegisterAndSerialize(kind: "replace_nested_conditional_with_guard_clauses",
            label: $"Replace nested conditional with guard clauses in {ownerFullName}.{containingMemberSignature}",
            changeSet, intent);
    }

    [McpServerTool(Name = "propose_consolidate_duplicate_conditional_fragments")]
    [Description("Propose Fowler's Consolidate Duplicate Conditional Fragments: hoist code that appears identically at the TOP or BOTTOM of both branches of an if-statement out of the if so it runs unconditionally. Selection points at the target if. MVP requires an else clause and refuses when nothing duplicates.")]
    public async Task<object> ProposeConsolidateDuplicateConditionalFragments(
        [Description("Fully-qualified name of the containing class.")] string ownerFullName,
        [Description("Signature of the method that contains the if-statement.")] string containingMemberSignature,
        [Description("Character offset (0-based) at the if-statement.")] int selectionStart,
        [Description("Length in characters (1 is fine — Kata locates the enclosing if).")] int selectionLength,
        CancellationToken cancellationToken,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.ConsolidateDuplicateConditionalFragments(
            owner, new MemberRef(owner, containingMemberSignature),
            selectionStart, System.Math.Max(1, selectionLength),
            IntentSource.Ai, rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        return RegisterAndSerialize(kind: "consolidate_duplicate_conditional_fragments",
            label: $"Consolidate duplicate conditional fragments in {ownerFullName}.{containingMemberSignature}",
            changeSet, intent);
    }

    [McpServerTool(Name = "propose_consolidate_conditional_expression")]
    [Description("Propose Fowler's Consolidate Conditional Expression: merge a run of consecutive if-statements that all execute the same body into one if guarded by the OR of their conditions. Selection covers the run. MVP requires each if to have no else clause AND all bodies to be syntactically identical (normalized text match).")]
    public async Task<object> ProposeConsolidateConditionalExpression(
        [Description("Fully-qualified name of the containing class.")] string ownerFullName,
        [Description("Signature of the method containing the run of ifs.")] string containingMemberSignature,
        [Description("Character offset (0-based) at the first if-statement.")] int selectionStart,
        [Description("Length in characters covering the run.")] int selectionLength,
        CancellationToken cancellationToken,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.ConsolidateConditionalExpression(
            owner, new MemberRef(owner, containingMemberSignature),
            selectionStart, System.Math.Max(1, selectionLength),
            IntentSource.Ai, rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        return RegisterAndSerialize(kind: "consolidate_conditional_expression",
            label: $"Consolidate conditional in {ownerFullName}.{containingMemberSignature}",
            changeSet, intent);
    }

    [McpServerTool(Name = "propose_decompose_conditional")]
    [Description("Propose Fowler's Decompose Conditional: extract the condition of an if-statement into a bool-returning method and each branch into a void method, then rewrite the if to call those methods. Selection points at the target IfStatementSyntax. DataFlowAnalysis infers per-method parameters. MVP: refuses branches with variables that flow out.")]
    public async Task<object> ProposeDecomposeConditional(
        [Description("Fully-qualified name of the containing class.")] string ownerFullName,
        [Description("Signature of the method containing the if-statement.")] string containingMemberSignature,
        [Description("Character offset (0-based) at the if-statement.")] int selectionStart,
        [Description("Length in characters (can be 1 — Kata locates the enclosing if).")] int selectionLength,
        [Description("Name for the extracted condition method (returns bool).")] string conditionMethodName,
        [Description("Name for the extracted then-branch method (void).")] string thenMethodName,
        CancellationToken cancellationToken,
        [Description("Optional name for the extracted else-branch method (void). Omit when the if has no else clause.")] string? elseMethodName = null,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.DecomposeConditional(
            owner, new MemberRef(owner, containingMemberSignature),
            selectionStart, System.Math.Max(1, selectionLength),
            conditionMethodName, thenMethodName,
            IntentSource.Ai, elseMethodName, rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        var label = $"Decompose conditional in {ownerFullName}.{containingMemberSignature} → {conditionMethodName} / {thenMethodName}" +
            (elseMethodName is null ? "" : $" / {elseMethodName}");
        return RegisterAndSerialize(kind: "decompose_conditional", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_extract_variable")]
    [Description("Propose Fowler's Extract Variable: lift a selected expression into a named local. Inserts `var {name} = {expr};` before the enclosing statement and replaces the selection with an identifier reference to it. Selection must be a valid ExpressionSyntax.")]
    public async Task<object> ProposeExtractVariable(
        [Description("Fully-qualified name of the containing class.")] string ownerFullName,
        [Description("Signature of the method that contains the expression.")] string containingMemberSignature,
        [Description("Character offset (0-based) where the expression begins.")] int selectionStart,
        [Description("Length in characters of the expression.")] int selectionLength,
        [Description("Name for the new local variable.")] string newVariableName,
        CancellationToken cancellationToken,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.ExtractVariable(
            owner, new MemberRef(owner, containingMemberSignature),
            selectionStart, selectionLength, newVariableName,
            IntentSource.Ai, rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        return RegisterAndSerialize(kind: "extract_variable",
            label: $"Extract variable {newVariableName} from {ownerFullName}.{containingMemberSignature}",
            changeSet, intent);
    }

    [McpServerTool(Name = "propose_inline_method")]
    [Description("Propose Fowler's Inline Method: replace a call to a small method with the method's body. MVP: target must be expression-bodied or a block body with a single `return expr;`. Only the ONE call site at the selection is inlined; the method declaration is left in place.")]
    public async Task<object> ProposeInlineMethod(
        [Description("Fully-qualified name of the class that contains the call site.")] string ownerFullName,
        [Description("Signature of the method that contains the call site.")] string containingMemberSignature,
        [Description("Character offset (0-based) into the call-site invocation.")] int selectionStart,
        [Description("Length in characters (may be zero if the caret sits inside the invocation).")] int selectionLength,
        CancellationToken cancellationToken,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.InlineMethod(
            owner, new MemberRef(owner, containingMemberSignature),
            selectionStart, System.Math.Max(1, selectionLength),
            IntentSource.Ai, rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        return RegisterAndSerialize(kind: "inline_method",
            label: $"Inline method at call site in {ownerFullName}.{containingMemberSignature}",
            changeSet, intent);
    }

    [McpServerTool(Name = "propose_inline_variable")]
    [Description("Propose Fowler's Inline Variable: replace every use of a local with its initializer expression, then delete the declaration. MVP requires the local to have an initializer AND to never be reassigned in the containing method — reassigned locals refuse.")]
    public async Task<object> ProposeInlineVariable(
        [Description("Fully-qualified name of the class that contains the local.")] string ownerFullName,
        [Description("Signature of the method that contains the local.")] string containingMemberSignature,
        [Description("Character offset (0-based) at the declaration or any use of the local.")] int selectionStart,
        [Description("Length in characters (may be zero if the caret sits on the identifier).")] int selectionLength,
        CancellationToken cancellationToken,
        [Description("Optional rationale.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.InlineVariable(
            owner, new MemberRef(owner, containingMemberSignature),
            selectionStart, System.Math.Max(1, selectionLength),
            IntentSource.Ai, rationale);
        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);
        return RegisterAndSerialize(kind: "inline_variable",
            label: $"Inline variable in {ownerFullName}.{containingMemberSignature}",
            changeSet, intent);
    }

    [McpServerTool(Name = "propose_extract_method")]
    [Description("Propose Fowler's Extract Method: pull a range of statements out of an existing method into a new method. Requires a character-offset range within the containing method's file (selectionStart / selectionLength). Roslyn DataFlowAnalysis infers parameters (variables read from the outer scope) and return type (a single variable assigned inside and used after becomes the return; otherwise void). Refuses control-flow-escaping selections (return / break / continue / goto / yield) and multi-output flows.")]
    public async Task<object> ProposeExtractMethod(
        [Description("Fully-qualified name of the class that contains the method being modified.")] string ownerFullName,
        [Description("Signature of the method to extract from (from get_type/list_types).")] string containingMemberSignature,
        [Description("Character offset (0-based) into the containing method's file where the selection begins.")] int selectionStart,
        [Description("Length in characters of the selection.")] int selectionLength,
        [Description("Name for the new extracted method.")] string newMethodName,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the extraction is proposed.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var intent = IntentFactory.ExtractMethod(
            ownerType: owner,
            containingMember: new MemberRef(owner, containingMemberSignature),
            selectionStart: selectionStart,
            selectionLength: selectionLength,
            newMethodName: newMethodName,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Extract method {newMethodName} from {ownerFullName}.{containingMemberSignature}";
        return RegisterAndSerialize(kind: "extract_method", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_convert_procedural_to_objects")]
    [Description("Propose Fowler's Convert Procedural Design to Objects: move static procedures that keep receiving the same data record as their first parameter onto that record as instance methods. For each method: drops the first parameter, rewrites references to it in the body as `this`, removes `static`, adds the transformed method to the data record type, deletes the original from the procedural class, and rewrites `Proc.M(record, x)` call sites to `record.M(x)`. Methods whose first param doesn't match the data record type are silently skipped.")]
    public async Task<object> ProposeConvertProceduralToObjects(
        [Description("Fully-qualified name of the static procedural class that currently owns the methods.")] string proceduralClassFullName,
        [Description("Fully-qualified name of the data record type that will receive the moved methods.")] string dataRecordTypeFullName,
        [Description("Signatures of the static methods to move (from get_type/list_types). Only methods whose first parameter is the data record type are moved.")] string[] methodSignatures,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the conversion is proposed.")] string? rationale = null)
    {
        var procedural = new TypeRef(proceduralClassFullName);
        var methods = methodSignatures.Select(sig => new MemberRef(procedural, sig)).ToArray();
        var intent = IntentFactory.ConvertProceduralToObjects(
            proceduralClass: procedural,
            dataRecordType: new TypeRef(dataRecordTypeFullName),
            methodsToMove: methods,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Move {methods.Length} procedure(s) from {proceduralClassFullName} onto {dataRecordTypeFullName}";
        return RegisterAndSerialize(kind: "convert_procedural_to_objects", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_tease_apart_inheritance")]
    [Description("Propose Fowler's Tease Apart Inheritance: when a class hierarchy varies on two orthogonal axes at once, scaffold a SECOND hierarchy for the extra axis and add a delegation field on the primary root. Emits: an abstract SecondaryHierarchyName class file, one shell subclass file per case, and appends `protected {Secondary}? {FieldName};` to the primary root. Migration of per-axis methods into the new subclasses (typically via Push Down Method) is left to the user.")]
    public async Task<object> ProposeTeaseApartInheritance(
        [Description("Fully-qualified name of the primary hierarchy root that gets the delegation field.")] string primaryHierarchyRootFullName,
        [Description("Name for the new secondary abstract class.")] string secondaryHierarchyName,
        [Description("Names of the secondary subclasses (one per case).")] string[] secondarySubclassNames,
        [Description("Name of the delegation field to add to the primary root (e.g. \"_side\").")] string delegationFieldName,
        CancellationToken cancellationToken,
        [Description("Optional target namespace for the new files. Defaults to the primary root's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why the teasing apart is proposed.")] string? rationale = null)
    {
        var intent = IntentFactory.TeaseApartInheritance(
            primaryHierarchyRoot: new TypeRef(primaryHierarchyRootFullName),
            secondaryHierarchyName: secondaryHierarchyName,
            secondarySubclassNames: secondarySubclassNames,
            delegationFieldName: delegationFieldName,
            source: IntentSource.Ai,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace),
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Tease {primaryHierarchyRootFullName} apart → {secondaryHierarchyName} + {secondarySubclassNames.Length} subclass(es), delegation field {delegationFieldName}";
        return RegisterAndSerialize(kind: "tease_apart_inheritance", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_extract_hierarchy")]
    [Description("Propose Fowler's Extract Hierarchy — also the mechanical action behind 'Replace Conditional with Polymorphism' (make the owner abstract, subclass per case, virtualize the methods that vary). Mark the owner class 'abstract' and create one subclass per case. Optionally, provide method signatures to virtualize — those methods become 'abstract' on the owner and each subclass gets an 'override' stub throwing NotImplementedException. Distinct from Replace Type Code with Subclasses in that this refactor is driven by polymorphism (methods vary by case), not a type-code field.")]
    public async Task<object> ProposeExtractHierarchy(
        [Description("Fully-qualified name of the owner class that will become the abstract root.")] string ownerFullName,
        [Description("Names of the subclasses to create (one per case, e.g. Circle, Square).")] string[] subclassNames,
        CancellationToken cancellationToken,
        [Description("Optional list of method signatures on the owner to make abstract + stub in every subclass. Signatures come from get_type/list_types.")] string[]? methodsToVirtualize = null,
        [Description("Optional target namespace for the new subclasses. Defaults to the owner's namespace.")] string? targetNamespace = null,
        [Description("Optional rationale — why the extraction is proposed.")] string? rationale = null)
    {
        var owner = new TypeRef(ownerFullName);
        var methods = methodsToVirtualize is null
            ? Array.Empty<MemberRef>()
            : methodsToVirtualize.Select(sig => new MemberRef(owner, sig)).ToArray();
        var intent = IntentFactory.ExtractHierarchy(
            ownerType: owner,
            subclassNames: subclassNames,
            source: IntentSource.Ai,
            methodsToVirtualize: methods,
            targetNamespace: targetNamespace is null ? null : new NamespaceRef(targetNamespace),
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = methods.Length == 0
            ? $"Extract hierarchy under {ownerFullName} → {string.Join(", ", subclassNames)}"
            : $"Extract hierarchy under {ownerFullName} → {string.Join(", ", subclassNames)}; virtualize {methods.Length} method(s)";
        return RegisterAndSerialize(kind: "extract_hierarchy", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_replace_subclass_with_fields")]
    [Description("Propose Fowler's Replace Subclass with Fields: drop 'abstract' from the parent and delete the named subclass files. Any type-code-varying behaviour on the subclasses must be pulled up into the parent (as fields or as a factory) BEFORE running this refactor.")]
    public async Task<object> ProposeReplaceSubclassWithFields(
        [Description("Fully-qualified name of the parent class.")] string parentFullName,
        [Description("Fully-qualified names of subclasses to delete.")] string[] subclassesToRemove,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why the flattening is proposed.")] string? rationale = null)
    {
        var intent = IntentFactory.ReplaceSubclassWithFields(
            parentType: new TypeRef(parentFullName),
            subclassesToRemove: subclassesToRemove.Select(s => new TypeRef(s)).ToArray(),
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Flatten hierarchy under {parentFullName} (delete {subclassesToRemove.Length} subclass(es))";
        return RegisterAndSerialize(kind: "replace_subclass_with_fields", label: label, changeSet, intent);
    }

    [McpServerTool(Name = "propose_add_ghost_type")]
    [Description("Propose adding a new type (class/interface/struct/record/enum) to the solution. The target project is inferred from the namespace prefix. Returns a pending change set adding one new source file.")]
    public async Task<object> ProposeAddGhostType(
        [Description("Name of the new type (without namespace).")] string typeName,
        [Description("Fully-qualified namespace for the new type. Must start with an existing project name.")] string namespaceName,
        [Description("Type kind: Class, Interface, Struct, Record, or Enum.")] string kind,
        CancellationToken cancellationToken,
        [Description("Optional rationale — why this new type is proposed.")] string? rationale = null)
    {
        if (!Enum.TryParse<TypeKind>(kind, ignoreCase: true, out var typeKind))
        {
            throw new InvalidOperationException($"Invalid kind '{kind}'. Use Class, Interface, Struct, Record, or Enum.");
        }

        var intent = IntentFactory.AddGhostType(
            proposedName: typeName,
            @namespace: new NamespaceRef(namespaceName),
            kind: typeKind,
            source: IntentSource.Ai,
            rationale: rationale);

        var changeSet = await RunAsync(intent, cancellationToken).ConfigureAwait(false);

        var label = $"Add {typeKind.ToString().ToLowerInvariant()} {namespaceName}.{typeName}";
        return RegisterAndSerialize(kind: "add_ghost_type", label: label, changeSet, intent);
    }

    private async Task<ChangeSet> RunAsync(RefactoringIntent intent, CancellationToken cancellationToken)
    {
        var model = await _session.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return await _session.Adapter
            .ProposeChangesAsync(model, new[] { intent }, cancellationToken)
            .ConfigureAwait(false);
    }

    private object RegisterAndSerialize(string kind, string label, ChangeSet changeSet, RefactoringIntent? intent = null)
    {
        var pending = _session.Register(kind, label, changeSet, intent);
        return new
        {
            changeSetId = pending.Id.ToString(),
            pending.Kind,
            pending.Label,
            summary = changeSet.Summary,
            filesAffected = changeSet.Changes.Count,
            changes = changeSet.Changes.Select(c => new
            {
                filePath = c.FilePath,
                kind = c.Kind.ToString(),
                oldText = c.OldText,
                newText = c.NewText,
            }).ToArray(),
        };
    }

    [McpServerTool(Name = "export_instruction")]
    [Description("Export a pending change set as a Markdown 'refactor instruction' file. Includes the summary, rationale, MCP tool call to reproduce, and per-file diffs. Team members can save this file and hand it to their own AI.")]
    public string ExportInstruction(
        [Description("Change set id returned by propose_* tools (Guid string).")] string changeSetId)
    {
        if (!Guid.TryParse(changeSetId, out var id))
        {
            throw new InvalidOperationException($"Not a valid change set id: {changeSetId}");
        }

        var pending = _session.RequirePending(id);
        return InstructionExporter.ExportMarkdown(
            pending.Intent,
            pending.ChangeSet,
            title: pending.Label,
            generatedAt: pending.CreatedAt.ToString("o"));
    }

    [McpServerTool(Name = "list_pending_changes")]
    [Description("List all pending change sets that have been proposed but not yet applied.")]
    public object ListPendingChanges()
    {
        return _session.ListPending().Select(p => new
        {
            changeSetId = p.Id.ToString(),
            p.Kind,
            p.Label,
            createdAt = p.CreatedAt.ToString("o"),
            filesAffected = p.ChangeSet.Changes.Count,
            summary = p.ChangeSet.Summary,
        }).ToArray();
    }

    [McpServerTool(Name = "apply_change_set")]
    [Description("Apply a previously-proposed change set to disk. This writes the files and reloads the solution. Removes the change set from pending on success.")]
    public async Task<object> ApplyChangeSet(
        [Description("Change set id returned by propose_* tools (Guid string).")] string changeSetId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(changeSetId, out var id))
        {
            throw new InvalidOperationException($"Not a valid change set id: {changeSetId}");
        }

        var pending = _session.RequirePending(id);
        var reloaded = await _session.Adapter
            .ApplyChangesAsync(pending.ChangeSet, cancellationToken)
            .ConfigureAwait(false);
        _session.UpdateModel(reloaded);
        _session.RemovePending(id);

        return new
        {
            appliedChangeSetId = id.ToString(),
            pending.Label,
            filesWritten = pending.ChangeSet.Changes.Select(c => c.FilePath).ToArray(),
            reloaded = new
            {
                path = reloaded.FilePath,
                projectCount = reloaded.Projects.Count,
                typeCount = reloaded.Projects.Sum(p => p.Types.Count),
            },
        };
    }

    [McpServerTool(Name = "discard_change_set")]
    [Description("Drop a pending change set without applying it. Use when a proposal is rejected.")]
    public object DiscardChangeSet(
        [Description("Change set id to drop (Guid string).")] string changeSetId)
    {
        if (!Guid.TryParse(changeSetId, out var id))
        {
            throw new InvalidOperationException($"Not a valid change set id: {changeSetId}");
        }

        var removed = _session.RemovePending(id);
        return new { discarded = removed, changeSetId = id.ToString() };
    }

    [McpServerTool(Name = "list_smells")]
    [Description("List detected code smells (Fowler's 24) from static analysis of the loaded solution. Optional filters restrict scope: typeFullName pins one type, memberSignature further pins one member, categoryName filters by SmellCategory. Auto-loads via session handshake if no solution is explicit.")]
    public async Task<object> ListSmells(
        [Description("Optional fully-qualified type name filter (e.g. 'Kata.App.Dialogs.RenameFieldDialog').")] string? typeFullName,
        [Description("Optional member signature filter (as returned by MemberRef.Signature). Requires typeFullName when set.")] string? memberSignature,
        [Description("Optional SmellCategory name filter (e.g. 'LongFunction'). Case-insensitive.")] string? categoryName,
        CancellationToken cancellationToken)
    {
        var index = await _session.GetSmellIndexAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<CodeSmell> smells = index.All;
        if (!string.IsNullOrWhiteSpace(typeFullName))
        {
            smells = smells.Where(s =>
                string.Equals(s.Type.FullyQualifiedName, typeFullName, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(memberSignature))
        {
            smells = smells.Where(s =>
                s.Member is { } m &&
                string.Equals(m.Signature, memberSignature, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(categoryName)
            && Enum.TryParse<SmellCategory>(categoryName, ignoreCase: true, out var wantedCategory))
        {
            smells = smells.Where(s => s.Category == wantedCategory);
        }

        return smells.Select(s => new
        {
            category = s.Category.ToString(),
            severity = s.Severity.ToString(),
            typeFullName = s.Type.FullyQualifiedName,
            memberSignature = s.Member?.Signature,
            message = s.Message,
        }).ToArray();
    }

    [McpServerTool(Name = "get_smell_context")]
    [Description("Return smells attached to a target plus the current source snippet for the offending member (or the type-level summary). Feed the smells + source into an LLM to produce a concrete refactor proposal, then call the matching propose_* tool. Auto-loads via session handshake.")]
    public async Task<object> GetSmellContext(
        [Description("Fully-qualified type name of the target.")] string typeFullName,
        [Description("Optional member signature. When supplied, returns the member source + smells attached to that specific member; otherwise returns type-level smells and the full type source.")] string? memberSignature,
        CancellationToken cancellationToken)
    {
        var model = await _session.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var index = await _session.GetSmellIndexAsync(cancellationToken).ConfigureAwait(false);
        var typeRef = new TypeRef(typeFullName);

        var typeModel = model.Projects
            .SelectMany(p => p.Types)
            .FirstOrDefault(t => t.Ref.Equals(typeRef))
            ?? throw new InvalidOperationException($"Type not found: {typeFullName}");

        MemberRef? memberRef = null;
        MemberModel? memberModel = null;
        if (!string.IsNullOrWhiteSpace(memberSignature))
        {
            memberModel = typeModel.Members.FirstOrDefault(m =>
                string.Equals(m.Ref.Signature, memberSignature, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Member not found on {typeFullName}: {memberSignature}");
            memberRef = memberModel.Ref;
        }

        var smells = memberRef is { } mr ? index.ForMember(mr) : index.TypeOnly(typeRef);

        string? sourceText = null;
        string? sourceFilePath = null;
        if (memberRef is { } mr2)
        {
            var src = await _session.Adapter
                .GetMemberSourceAsync(model, typeRef, mr2, cancellationToken)
                .ConfigureAwait(false);
            sourceText = src?.SourceText;
            sourceFilePath = src?.FilePath;
        }

        return new
        {
            typeFullName = typeRef.FullyQualifiedName,
            typeName = typeModel.Name,
            @namespace = typeModel.Namespace.FullName,
            memberSignature = memberRef?.Signature,
            memberName = memberModel?.Name,
            sourceFilePath,
            sourceText,
            smells = smells.Select(s => new
            {
                category = s.Category.ToString(),
                severity = s.Severity.ToString(),
                message = s.Message,
            }).ToArray(),
        };
    }

    // === AI-smell task queue ===
    // Kata.App (or any triggering client) enqueues an analysis request; an AI-agent client
    // polls list_pending_ai_smell_tasks, produces a proposal via its own LLM, then calls
    // complete_ai_smell_task. Kata.App poll get_ai_smell_task for status/result. Stateless
    // multi-client coordination without any Sampling round-trip.

    [McpServerTool(Name = "request_ai_smell_analysis")]
    [Description("Enqueue an AI analysis task for a smell target. Returns a task id; poll get_ai_smell_task for status/result. Intended for triggers like the 'AI に相談' button in Kata.App.")]
    public object RequestAiSmellAnalysis(
        [Description("Fully-qualified type name of the smell target.")] string typeFullName,
        [Description("Optional member signature when the smell is member-scoped.")] string? memberSignature,
        [Description("SmellCategory name (e.g. 'LongFunction') describing the smell to analyse.")] string category,
        [Description("Free-form prompt or hint the AI agent should use when analysing the target.")] string prompt)
    {
        var task = _aiTasks.Enqueue(typeFullName, memberSignature, category, prompt);
        return new
        {
            taskId = task.Id.ToString(),
            status = task.Status.ToString(),
            createdAt = task.CreatedAt,
        };
    }

    [McpServerTool(Name = "list_pending_ai_smell_tasks")]
    [Description("List AI smell-analysis tasks awaiting an agent. Called by the AI agent to discover work. Each task carries the target (typeFullName/memberSignature), the smell category, and a prompt hint.")]
    public object ListPendingAiSmellTasks()
    {
        return _aiTasks.Pending().Select(t => new
        {
            taskId = t.Id.ToString(),
            createdAt = t.CreatedAt,
            typeFullName = t.TypeFullName,
            memberSignature = t.MemberSignature,
            category = t.Category,
            prompt = t.Prompt,
        }).ToArray();
    }

    [McpServerTool(Name = "get_ai_smell_task")]
    [Description("Fetch the current status and (if done) the result of an AI smell-analysis task. Trigger clients poll this to wait for the agent's proposal.")]
    public object GetAiSmellTask(
        [Description("Task id returned by request_ai_smell_analysis.")] string taskId)
    {
        if (!Guid.TryParse(taskId, out var id))
            throw new InvalidOperationException($"Not a valid task id: {taskId}");
        var task = _aiTasks.TryGet(id)
            ?? throw new InvalidOperationException($"No AI smell task with id {taskId}");

        return new
        {
            taskId = task.Id.ToString(),
            status = task.Status.ToString(),
            createdAt = task.CreatedAt,
            completedAt = task.CompletedAt,
            typeFullName = task.TypeFullName,
            memberSignature = task.MemberSignature,
            category = task.Category,
            result = task.Result,
        };
    }

    [McpServerTool(Name = "complete_ai_smell_task")]
    [Description("Mark an AI smell-analysis task as complete and attach the agent-produced proposal payload (usually a JSON block describing the refactor recommendation).")]
    public object CompleteAiSmellTask(
        [Description("Task id.")] string taskId,
        [Description("Proposal payload — a JSON blob (or plain text) with the AI's recommendation.")] string proposal)
    {
        if (!Guid.TryParse(taskId, out var id))
            throw new InvalidOperationException($"Not a valid task id: {taskId}");
        var ok = _aiTasks.Complete(id, proposal);
        return new { taskId, accepted = ok };
    }

    [McpServerTool(Name = "fail_ai_smell_task")]
    [Description("Mark an AI smell-analysis task as failed with a short reason string. Use when the agent cannot produce a proposal (unresolvable, out of scope, etc.).")]
    public object FailAiSmellTask(
        [Description("Task id.")] string taskId,
        [Description("Short failure reason.")] string reason)
    {
        if (!Guid.TryParse(taskId, out var id))
            throw new InvalidOperationException($"Not a valid task id: {taskId}");
        var ok = _aiTasks.Fail(id, reason);
        return new { taskId, accepted = ok };
    }

    private static object SerializeType(string projectName, TypeModel type) => new
    {
        projectName,
        fullName = type.Ref.FullyQualifiedName,
        type.Name,
        @namespace = type.Namespace.FullName,
        kind = type.Kind.ToString(),
        accessibility = type.Accessibility.ToString(),
        baseTypes = type.BaseTypes.Select(t => t.FullyQualifiedName).ToArray(),
        implementedInterfaces = type.ImplementedInterfaces.Select(t => t.FullyQualifiedName).ToArray(),
        members = type.Members.Select(m => new
        {
            m.Name,
            kind = m.Kind.ToString(),
            accessibility = m.Accessibility.ToString(),
            m.ReturnTypeDisplay,
            m.IsStatic,
            m.IsGhost,
        }).ToArray(),
        type.IsGhost,
    };
}
