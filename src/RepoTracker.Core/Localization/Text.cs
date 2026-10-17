using System.Globalization;
using System.Resources;

namespace RepoTracker.Core.Localization;

/// <summary>UI text only. Never change the user's regional formatting or Git's protocol culture.</summary>
public static class Text
{
    public const string SystemLanguage = "system";
    public static readonly ResourceManager Resources = new("RepoTracker.Core.Localization.Strings", typeof(Text).Assembly);
    private static CultureInfo? selectedCulture;

    public static CultureInfo SelectLanguage(string preference, CultureInfo systemDisplayCulture)
        => selectedCulture = ResolveCulture(preference, systemDisplayCulture);

    public static bool IsSupportedPreference(string? language) => language is SystemLanguage or "zh-Hans" or "en";

    public static CultureInfo ResolveCulture(string? preference, CultureInfo systemDisplayCulture)
    {
        if (preference == "zh-Hans") return CultureInfo.GetCultureInfo("zh-Hans");
        if (preference == "en") return CultureInfo.GetCultureInfo("en");
        // Traditional Chinese has no translation yet; do not silently claim support for it.
        for (var culture = systemDisplayCulture; culture.Name.Length > 0; culture = culture.Parent)
        {
            if (culture.Name.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo("en");
            if (culture.Name.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo("zh-Hans");
        }
        return CultureInfo.GetCultureInfo(systemDisplayCulture.Name.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en");
    }

    public static string Get(string key) => Get(key, selectedCulture ?? ResolveCulture(SystemLanguage, CultureInfo.CurrentUICulture));

    public static string Get(string key, CultureInfo culture) => Resources.GetString(key, ResolveCulture(SystemLanguage, culture))
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");

    public static string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
