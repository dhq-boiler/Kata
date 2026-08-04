using System.IO;
using Kata.Core.Model;

namespace Kata.App.ViewModels;

public sealed class ReferenceRowViewModel
{
    public ReferenceLocation Location { get; }

    public ReferenceRowViewModel(ReferenceLocation location)
    {
        Location = location;
    }

    public string LanguageBadge => Location.Language switch
    {
        ReferenceLanguage.CppCli => "[C++]",
        ReferenceLanguage.CSharp => "[C#]",
        _ => "[?]",
    };

    public string FullPath => Location.FilePath;
    public string FileLine => string.IsNullOrEmpty(Location.FilePath)
        ? $":{Location.Line}"
        : $"{Path.GetFileName(Location.FilePath)}:{Location.Line}";

    public string Snippet => Location.LineSnippet;
}
