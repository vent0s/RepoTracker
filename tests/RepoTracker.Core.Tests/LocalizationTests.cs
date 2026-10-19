using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using RepoTracker.Core.Localization;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("system", "zh-CN", "zh-Hans")]
    [InlineData("system", "zh-SG", "zh-Hans")]
    [InlineData("system", "zh-Hans", "zh-Hans")]
    [InlineData("system", "zh", "zh-Hans")]
    [InlineData("system", "en-US", "en")]
    [InlineData("system", "en-GB", "en")]
    [InlineData("system", "fr-FR", "en")]
    [InlineData("system", "zh-TW", "en")]
    [InlineData("system", "", "en")]
    [InlineData("zh-Hans", "en-US", "zh-Hans")]
    [InlineData("en", "zh-CN", "en")]
    public void DisplayLanguageResolution(string preference, string system, string expected)
        => Assert.Equal(expected, Text.ResolveCulture(preference, CultureInfo.GetCultureInfo(system)).Name);

    [Fact]
    public void DefaultRemainsSystemInsteadOfSavingCurrentMachineLanguage()
        => Assert.Equal("system", new AppPreferences().Language);

    [Fact]
    public void EveryTranslationHasTheSamePlaceholdersAndCanBeFormatted()
    {
        var english = Text.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var chinese = Text.Resources.GetResourceSet(CultureInfo.GetCultureInfo("zh-Hans"), true, false)!;
        var entries = english.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var translations = chinese.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        Assert.Equal(entries.Keys.Order().ToArray(), translations.Keys.Order().ToArray());
        foreach (var (key, value) in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(translations[key]), key);
            static string[] Arguments(string text) => Regex.Matches(text, @"\{(\d+)(?:[^}]*)\}").Select(m => m.Groups[1].Value).Order().ToArray();
            Assert.Equal(Arguments(value), Arguments(translations[key]));
            _ = string.Format(CultureInfo.InvariantCulture, value, new object[] { "repo", 2, 3, 4, 5, 6, 7, 8 });
            _ = string.Format(CultureInfo.InvariantCulture, translations[key], new object[] { "仓库", 2, 3, 4, 5, 6, 7, 8 });
        }
        Assert.Equal("Settings", Text.Get("Settings.Heading", CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("设置", Text.Get("Settings.Heading", CultureInfo.GetCultureInfo("zh-CN")));
    }

    [Fact]
    public void LanguageResolutionDoesNotChangeRegionalNumberFormatting()
    {
        var before = CultureInfo.CurrentCulture;
        _ = Text.ResolveCulture("en", CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Same(before, CultureInfo.CurrentCulture);
        Assert.Throws<TrackerException>(() => new AppPreferences { Language = "invalid" }.Validate());
    }
}
