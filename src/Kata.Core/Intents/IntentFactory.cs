using Kata.Core.Model;

namespace Kata.Core.Intents;

public static class IntentFactory
{
    public static RenameIntent Rename(
        TypeRef targetType,
        string newName,
        IntentSource source,
        string? rationale = null,
        MemberRef? targetMember = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            TargetType = targetType,
            TargetMember = targetMember,
            NewName = newName,
        };

    public static ExtractInterfaceIntent ExtractInterface(
        TypeRef sourceType,
        IReadOnlyList<MemberRef> members,
        string proposedInterfaceName,
        IntentSource source,
        string? rationale = null,
        NamespaceRef? targetNamespace = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            SourceType = sourceType,
            Members = members,
            ProposedInterfaceName = proposedInterfaceName,
            TargetNamespace = targetNamespace,
        };

    public static ExtractSuperclassIntent ExtractSuperclass(
        TypeRef sourceType,
        IReadOnlyList<MemberRef> members,
        string proposedSuperclassName,
        IntentSource source,
        string? rationale = null,
        NamespaceRef? targetNamespace = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            SourceType = sourceType,
            Members = members,
            ProposedSuperclassName = proposedSuperclassName,
            TargetNamespace = targetNamespace,
        };

    public static ExtractClassIntent ExtractClass(
        TypeRef sourceType,
        IReadOnlyList<MemberRef> members,
        string proposedClassName,
        string delegatePropertyName,
        IntentSource source,
        string? rationale = null,
        NamespaceRef? targetNamespace = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            SourceType = sourceType,
            Members = members,
            ProposedClassName = proposedClassName,
            DelegatePropertyName = delegatePropertyName,
            TargetNamespace = targetNamespace,
        };

    public static CollapseHierarchyIntent CollapseHierarchy(
        TypeRef subclass,
        TypeRef parent,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            Subclass = subclass,
            Parent = parent,
        };

    public static RemoveSubclassIntent RemoveSubclass(
        TypeRef subclass,
        TypeRef replacementBase,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            Subclass = subclass,
            ReplacementBase = replacementBase,
        };

    public static PullUpMethodIntent PullUpMethod(
        TypeRef subclass,
        TypeRef parent,
        IReadOnlyList<MemberRef> members,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            Subclass = subclass,
            Parent = parent,
            Members = members,
        };

    public static PushDownMethodIntent PushDownMethod(
        TypeRef parent,
        TypeRef subclass,
        IReadOnlyList<MemberRef> members,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            Parent = parent,
            Subclass = subclass,
            Members = members,
        };

    public static PullUpFieldIntent PullUpField(
        TypeRef subclass,
        TypeRef parent,
        IReadOnlyList<MemberRef> members,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            Subclass = subclass,
            Parent = parent,
            Members = members,
        };

    public static PushDownFieldIntent PushDownField(
        TypeRef parent,
        TypeRef subclass,
        IReadOnlyList<MemberRef> members,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            Parent = parent,
            Subclass = subclass,
            Members = members,
        };

    public static RemoveSettingMethodIntent RemoveSettingMethod(
        TypeRef ownerType,
        MemberRef property,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Property = property,
        };

    public static RenameFieldIntent RenameField(
        TypeRef ownerType,
        MemberRef field,
        string newName,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Field = field,
            NewName = newName,
        };

    public static PullUpConstructorBodyIntent PullUpConstructorBody(
        TypeRef subclass,
        TypeRef parent,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            Subclass = subclass,
            Parent = parent,
        };

    public static EncapsulateFieldIntent EncapsulateField(
        TypeRef ownerType,
        MemberRef field,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Field = field,
        };

    public static MoveMethodIntent MoveMethod(
        TypeRef sourceType,
        TypeRef targetType,
        IReadOnlyList<MemberRef> members,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            SourceType = sourceType,
            TargetType = targetType,
            Members = members,
        };

    public static MoveFieldIntent MoveField(
        TypeRef sourceType,
        TypeRef targetType,
        IReadOnlyList<MemberRef> members,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            SourceType = sourceType,
            TargetType = targetType,
            Members = members,
        };

    public static ReplaceConstructorWithFactoryIntent ReplaceConstructorWithFactory(
        TypeRef ownerType,
        IntentSource source,
        string factoryName = "Create",
        bool makeConstructorPrivate = true,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            FactoryName = factoryName,
            MakeConstructorPrivate = makeConstructorPrivate,
        };

    public static ReplaceMagicNumberIntent ReplaceMagicNumber(
        TypeRef ownerType,
        string literalValue,
        string constantName,
        IntentSource source,
        string constantType = "int",
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            LiteralValue = literalValue,
            ConstantName = constantName,
            ConstantType = constantType,
        };

    public static ChangeBidirectionalToUnidirectionalIntent ChangeBidirectionalToUnidirectional(
        TypeRef ownerType,
        MemberRef field,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Field = field,
        };

    public static IntroduceParameterObjectIntent IntroduceParameterObject(
        TypeRef ownerType,
        MemberRef method,
        string proposedObjectName,
        IntentSource source,
        string parameterName = "args",
        NamespaceRef? targetNamespace = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Method = method,
            ProposedObjectName = proposedObjectName,
            ParameterName = parameterName,
            TargetNamespace = targetNamespace,
        };

    public static AddParameterIntent AddParameter(
        TypeRef ownerType,
        MemberRef method,
        string parameterType,
        string parameterName,
        IntentSource source,
        string? defaultValue = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Method = method,
            ParameterType = parameterType,
            ParameterName = parameterName,
            DefaultValue = defaultValue,
        };

    public static RemoveParameterIntent RemoveParameter(
        TypeRef ownerType,
        MemberRef method,
        string parameterName,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Method = method,
            ParameterName = parameterName,
        };

    public static ReplaceDataValueWithObjectIntent ReplaceDataValueWithObject(
        TypeRef ownerType,
        MemberRef field,
        string wrapperClassName,
        IntentSource source,
        string innerFieldName = "Value",
        NamespaceRef? targetNamespace = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Field = field,
            WrapperClassName = wrapperClassName,
            InnerFieldName = innerFieldName,
            TargetNamespace = targetNamespace,
        };

    public static RenameParameterIntent RenameParameter(
        TypeRef ownerType,
        MemberRef method,
        string oldName,
        string newName,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Method = method,
            OldName = oldName,
            NewName = newName,
        };

    public static SelfEncapsulateFieldIntent SelfEncapsulateField(
        TypeRef ownerType,
        MemberRef field,
        IntentSource source,
        string? propertyName = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Field = field,
            PropertyName = propertyName,
        };

    public static ChangeReferenceToValueIntent ChangeReferenceToValue(
        TypeRef ownerType,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
        };

    public static ChangeValueToReferenceIntent ChangeValueToReference(
        TypeRef ownerType,
        IntentSource source,
        string keyType = "string",
        string factoryName = "GetOrCreate",
        string registryFieldName = "_instances",
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            KeyType = keyType,
            FactoryName = factoryName,
            RegistryFieldName = registryFieldName,
        };

    public static ReplaceTypeCodeWithClassIntent ReplaceTypeCodeWithClass(
        TypeRef ownerType,
        MemberRef field,
        string newClassName,
        IReadOnlyList<TypeCodeEntry> codes,
        IntentSource source,
        string innerCodeType = "int",
        NamespaceRef? targetNamespace = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Field = field,
            NewClassName = newClassName,
            Codes = codes,
            InnerCodeType = innerCodeType,
            TargetNamespace = targetNamespace,
        };

    public static PreserveWholeObjectIntent PreserveWholeObject(
        TypeRef ownerType,
        MemberRef method,
        TypeRef objectType,
        string parameterName,
        IReadOnlyList<string> replacedParameterNames,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            Method = method,
            ObjectType = objectType,
            ParameterName = parameterName,
            ReplacedParameterNames = replacedParameterNames,
        };

    public static ReplaceArrayWithObjectIntent ReplaceArrayWithObject(
        TypeRef ownerType,
        MemberRef arrayField,
        string newClassName,
        IReadOnlyList<ArrayFieldMapping> fieldMappings,
        IntentSource source,
        NamespaceRef? targetNamespace = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ArrayField = arrayField,
            NewClassName = newClassName,
            FieldMappings = fieldMappings,
            TargetNamespace = targetNamespace,
        };

    public static ReplaceTypeCodeWithSubclassesIntent ReplaceTypeCodeWithSubclasses(
        TypeRef ownerType,
        IReadOnlyList<string> subclassNames,
        IntentSource source,
        NamespaceRef? targetNamespace = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            SubclassNames = subclassNames,
            TargetNamespace = targetNamespace,
        };

    public static IntroduceAssertionIntent IntroduceAssertion(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        string assertionExpression,
        IntentSource source,
        string? message = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            AssertionExpression = assertionExpression,
            Message = message,
        };

    public static IntroduceNullObjectIntent IntroduceNullObject(
        TypeRef sourceType,
        IntentSource source,
        string? nullClassName = null,
        NamespaceRef? targetNamespace = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            SourceType = sourceType,
            NullClassName = nullClassName,
            TargetNamespace = targetNamespace,
        };

    public static ReplaceNestedConditionalWithGuardClausesIntent ReplaceNestedConditionalWithGuardClauses(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength,
        };

    public static ConsolidateDuplicateConditionalFragmentsIntent ConsolidateDuplicateConditionalFragments(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength,
        };

    public static ConsolidateConditionalExpressionIntent ConsolidateConditionalExpression(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength,
        };

    public static DecomposeConditionalIntent DecomposeConditional(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string conditionMethodName,
        string thenMethodName,
        IntentSource source,
        string? elseMethodName = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength,
            ConditionMethodName = conditionMethodName,
            ThenMethodName = thenMethodName,
            ElseMethodName = elseMethodName,
        };

    public static InlineMethodIntent InlineMethod(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength,
        };

    public static InlineVariableIntent InlineVariable(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength,
        };

    public static ExtractVariableIntent ExtractVariable(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string newVariableName,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength,
            NewVariableName = newVariableName,
        };

    public static ExtractMethodIntent ExtractMethod(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string newMethodName,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            ContainingMember = containingMember,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength,
            NewMethodName = newMethodName,
        };

    public static ConvertProceduralToObjectsIntent ConvertProceduralToObjects(
        TypeRef proceduralClass,
        TypeRef dataRecordType,
        IReadOnlyList<MemberRef> methodsToMove,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            ProceduralClass = proceduralClass,
            DataRecordType = dataRecordType,
            MethodsToMove = methodsToMove,
        };

    public static TeaseApartInheritanceIntent TeaseApartInheritance(
        TypeRef primaryHierarchyRoot,
        string secondaryHierarchyName,
        IReadOnlyList<string> secondarySubclassNames,
        string delegationFieldName,
        IntentSource source,
        NamespaceRef? targetNamespace = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            PrimaryHierarchyRoot = primaryHierarchyRoot,
            SecondaryHierarchyName = secondaryHierarchyName,
            SecondarySubclassNames = secondarySubclassNames,
            DelegationFieldName = delegationFieldName,
            TargetNamespace = targetNamespace,
        };

    public static ExtractHierarchyIntent ExtractHierarchy(
        TypeRef ownerType,
        IReadOnlyList<string> subclassNames,
        IntentSource source,
        IReadOnlyList<MemberRef>? methodsToVirtualize = null,
        NamespaceRef? targetNamespace = null,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            OwnerType = ownerType,
            SubclassNames = subclassNames,
            MethodsToVirtualize = methodsToVirtualize ?? Array.Empty<MemberRef>(),
            TargetNamespace = targetNamespace,
        };

    public static ReplaceSubclassWithFieldsIntent ReplaceSubclassWithFields(
        TypeRef parentType,
        IReadOnlyList<TypeRef> subclassesToRemove,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            ParentType = parentType,
            SubclassesToRemove = subclassesToRemove,
        };

    public static AddGhostTypeIntent AddGhostType(
        string proposedName,
        NamespaceRef @namespace,
        TypeKind kind,
        IntentSource source,
        string? rationale = null)
        => new()
        {
            Source = source,
            Rationale = rationale,
            ProposedName = proposedName,
            Namespace = @namespace,
            Kind = kind,
        };
}
