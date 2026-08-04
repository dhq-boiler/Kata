using Kata.Core.Intents;
using Kata.Core.Model;

namespace Kata.Core.Diff;

public sealed record McpCall(string ToolName, IReadOnlyList<KeyValuePair<string, object?>> Arguments);

public static class McpCallDescriptor
{
    public static McpCall? Describe(RefactoringIntent intent) => intent switch
    {
        RenameIntent r => new("propose_rename", new[]
        {
            Pair("typeFullName", r.TargetType.FullyQualifiedName),
            Pair("newName", r.NewName),
            Pair("memberSignature", r.TargetMember?.Signature),
            Pair("rationale", r.Rationale),
        }),
        ExtractInterfaceIntent i => new("propose_extract_interface", new[]
        {
            Pair("typeFullName", i.SourceType.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("interfaceName", i.ProposedInterfaceName),
            Pair("targetNamespace", i.TargetNamespace?.FullName),
            Pair("rationale", i.Rationale),
        }),
        ExtractSuperclassIntent i => new("propose_extract_superclass", new[]
        {
            Pair("typeFullName", i.SourceType.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("superclassName", i.ProposedSuperclassName),
            Pair("targetNamespace", i.TargetNamespace?.FullName),
            Pair("rationale", i.Rationale),
        }),
        ExtractClassIntent i => new("propose_extract_class", new[]
        {
            Pair("typeFullName", i.SourceType.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("newClassName", i.ProposedClassName),
            Pair("delegatePropertyName", i.DelegatePropertyName),
            Pair("targetNamespace", i.TargetNamespace?.FullName),
            Pair("rationale", i.Rationale),
        }),
        RemoveSubclassIntent i => new("propose_remove_subclass", new[]
        {
            Pair("subclassFullName", i.Subclass.FullyQualifiedName),
            Pair("replacementBaseFullName", i.ReplacementBase.FullyQualifiedName),
            Pair("rationale", i.Rationale),
        }),
        CollapseHierarchyIntent i => new("propose_collapse_hierarchy", new[]
        {
            Pair("subclassFullName", i.Subclass.FullyQualifiedName),
            Pair("parentFullName", i.Parent.FullyQualifiedName),
            Pair("rationale", i.Rationale),
        }),
        PullUpMethodIntent i => new("propose_pull_up_method", new[]
        {
            Pair("subclassFullName", i.Subclass.FullyQualifiedName),
            Pair("parentFullName", i.Parent.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("rationale", i.Rationale),
        }),
        PushDownMethodIntent i => new("propose_push_down_method", new[]
        {
            Pair("parentFullName", i.Parent.FullyQualifiedName),
            Pair("subclassFullName", i.Subclass.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("rationale", i.Rationale),
        }),
        PullUpFieldIntent i => new("propose_pull_up_field", new[]
        {
            Pair("subclassFullName", i.Subclass.FullyQualifiedName),
            Pair("parentFullName", i.Parent.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("rationale", i.Rationale),
        }),
        PushDownFieldIntent i => new("propose_push_down_field", new[]
        {
            Pair("parentFullName", i.Parent.FullyQualifiedName),
            Pair("subclassFullName", i.Subclass.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("rationale", i.Rationale),
        }),
        RemoveSettingMethodIntent i => new("propose_remove_setting_method", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("propertySignature", i.Property.Signature),
            Pair("rationale", i.Rationale),
        }),
        RenameFieldIntent i => new("propose_rename_field", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("fieldSignature", i.Field.Signature),
            Pair("newName", i.NewName),
            Pair("rationale", i.Rationale),
        }),
        PullUpConstructorBodyIntent i => new("propose_pull_up_constructor_body", new[]
        {
            Pair("subclassFullName", i.Subclass.FullyQualifiedName),
            Pair("parentFullName", i.Parent.FullyQualifiedName),
            Pair("rationale", i.Rationale),
        }),
        EncapsulateFieldIntent i => new("propose_encapsulate_field", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("fieldSignature", i.Field.Signature),
            Pair("rationale", i.Rationale),
        }),
        MoveMethodIntent i => new("propose_move_method", new[]
        {
            Pair("sourceFullName", i.SourceType.FullyQualifiedName),
            Pair("targetFullName", i.TargetType.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("rationale", i.Rationale),
        }),
        MoveFieldIntent i => new("propose_move_field", new[]
        {
            Pair("sourceFullName", i.SourceType.FullyQualifiedName),
            Pair("targetFullName", i.TargetType.FullyQualifiedName),
            Pair("memberSignatures", i.Members.Select(m => m.Signature).ToArray()),
            Pair("rationale", i.Rationale),
        }),
        ReplaceConstructorWithFactoryIntent i => new("propose_replace_constructor_with_factory", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("factoryName", i.FactoryName),
            Pair("makeConstructorPrivate", i.MakeConstructorPrivate),
            Pair("rationale", i.Rationale),
        }),
        ReplaceMagicNumberIntent i => new("propose_replace_magic_number", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("literalValue", i.LiteralValue),
            Pair("constantName", i.ConstantName),
            Pair("constantType", i.ConstantType),
            Pair("rationale", i.Rationale),
        }),
        ChangeBidirectionalToUnidirectionalIntent i => new("propose_change_bidirectional_to_unidirectional", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("fieldSignature", i.Field.Signature),
            Pair("rationale", i.Rationale),
        }),
        IntroduceParameterObjectIntent i => new("propose_introduce_parameter_object", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("methodSignature", i.Method.Signature),
            Pair("proposedObjectName", i.ProposedObjectName),
            Pair("parameterName", i.ParameterName),
            Pair("targetNamespace", i.TargetNamespace?.FullName),
            Pair("rationale", i.Rationale),
        }),
        AddParameterIntent i => new("propose_add_parameter", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("methodSignature", i.Method.Signature),
            Pair("parameterType", i.ParameterType),
            Pair("parameterName", i.ParameterName),
            Pair("defaultValue", i.DefaultValue),
            Pair("rationale", i.Rationale),
        }),
        RemoveParameterIntent i => new("propose_remove_parameter", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("methodSignature", i.Method.Signature),
            Pair("parameterName", i.ParameterName),
            Pair("rationale", i.Rationale),
        }),
        ReplaceDataValueWithObjectIntent i => new("propose_replace_data_value_with_object", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("fieldSignature", i.Field.Signature),
            Pair("wrapperClassName", i.WrapperClassName),
            Pair("innerFieldName", i.InnerFieldName),
            Pair("targetNamespace", i.TargetNamespace?.FullName),
            Pair("rationale", i.Rationale),
        }),
        RenameParameterIntent i => new("propose_rename_parameter", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("methodSignature", i.Method.Signature),
            Pair("oldName", i.OldName),
            Pair("newName", i.NewName),
            Pair("rationale", i.Rationale),
        }),
        SelfEncapsulateFieldIntent i => new("propose_self_encapsulate_field", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("fieldSignature", i.Field.Signature),
            Pair("propertyName", i.PropertyName),
            Pair("rationale", i.Rationale),
        }),
        ChangeReferenceToValueIntent i => new("propose_change_reference_to_value", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("rationale", i.Rationale),
        }),
        ChangeValueToReferenceIntent i => new("propose_change_value_to_reference", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("keyType", i.KeyType),
            Pair("factoryName", i.FactoryName),
            Pair("registryFieldName", i.RegistryFieldName),
            Pair("rationale", i.Rationale),
        }),
        ReplaceTypeCodeWithClassIntent i => new("propose_replace_type_code_with_class", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("fieldSignature", i.Field.Signature),
            Pair("newClassName", i.NewClassName),
            Pair("codeEntries", i.Codes.Select(c => $"{c.Name}={c.Value}").ToArray()),
            Pair("innerCodeType", i.InnerCodeType),
            Pair("targetNamespace", i.TargetNamespace?.FullName),
            Pair("rationale", i.Rationale),
        }),
        PreserveWholeObjectIntent i => new("propose_preserve_whole_object", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("methodSignature", i.Method.Signature),
            Pair("objectFullName", i.ObjectType.FullyQualifiedName),
            Pair("parameterName", i.ParameterName),
            Pair("replacedParameterNames", i.ReplacedParameterNames.ToArray()),
            Pair("rationale", i.Rationale),
        }),
        ReplaceArrayWithObjectIntent i => new("propose_replace_array_with_object", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("fieldSignature", i.ArrayField.Signature),
            Pair("newClassName", i.NewClassName),
            Pair("fieldMappings", i.FieldMappings.Select(m => $"{m.Index}:{m.FieldName}:{m.FieldType}").ToArray()),
            Pair("targetNamespace", i.TargetNamespace?.FullName),
            Pair("rationale", i.Rationale),
        }),
        ReplaceTypeCodeWithSubclassesIntent i => new("propose_replace_type_code_with_subclasses", new[]
        {
            Pair("ownerFullName", i.OwnerType.FullyQualifiedName),
            Pair("subclassNames", i.SubclassNames.ToArray()),
            Pair("targetNamespace", i.TargetNamespace?.FullName),
            Pair("rationale", i.Rationale),
        }),
        ReplaceSubclassWithFieldsIntent i => new("propose_replace_subclass_with_fields", new[]
        {
            Pair("parentFullName", i.ParentType.FullyQualifiedName),
            Pair("subclassesToRemove", i.SubclassesToRemove.Select(s => s.FullyQualifiedName).ToArray()),
            Pair("rationale", i.Rationale),
        }),
        AddGhostTypeIntent i => new("propose_add_ghost_type", new[]
        {
            Pair("typeName", i.ProposedName),
            Pair("namespaceName", i.Namespace.FullName),
            Pair("kind", i.Kind.ToString()),
            Pair("rationale", i.Rationale),
        }),
        _ => null,
    };

    private static KeyValuePair<string, object?> Pair(string k, object? v) => new(k, v);
}
