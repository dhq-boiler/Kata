using CommunityToolkit.Mvvm.ComponentModel;
using Kata.Core.Model;

namespace Kata.App.ViewModels;

public sealed partial class MemberSourceViewModel : ObservableObject
{
    public MemberSourceViewModel(MemberSource source)
    {
        Source = source;
        _sourceText = source.SourceText;
        _filePath = source.FilePath;
        _memberSignature = source.Member.Signature;
        _ownerTypeName = source.OwnerType.FullyQualifiedName;
    }

    public MemberSource Source { get; }

    [ObservableProperty] private string _sourceText;
    [ObservableProperty] private string _filePath;
    [ObservableProperty] private string _memberSignature;
    [ObservableProperty] private string _ownerTypeName;
    [ObservableProperty] private int _selectionStart;
    [ObservableProperty] private int _selectionLength;
    [ObservableProperty] private string _selectedText = string.Empty;

    public int BodySpanStart => Source.BodySpanStart;
    public int BodySpanLength => Source.BodySpanLength;

    public bool HasSelection => SelectionLength > 0;
}
