using System.Windows.Markup;
using RepoTracker.Core.Localization;

namespace RepoTracker.Desktop.Localization;

/// <summary>Language is selected before windows are constructed and changes on the next launch.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Text.Get(Key);
}
