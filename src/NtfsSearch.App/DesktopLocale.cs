using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NtfsSearch.Core;

namespace NtfsSearch.App;

// 保留消息参数直到发布，切换语言时可以重新渲染当前进度，无需重新采集。
internal sealed record DesktopText(string Key, params (string Name, object Value)[] Arguments);

internal sealed class DesktopLocale
{
    private const string English = "en";
    private const string SimplifiedChinese = "zh-CN";
    private const string ResourcePrefix = "Locales.";
    private const string ResourceExtension = ".json";
    private static readonly Regex Placeholder = new(@"\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, DesktopLocale> Locales = LoadLocales();
    private static readonly string SystemLanguage = Environment.GetEnvironmentVariable("NTFS_SEARCH_UI_LANGUAGE") is { Length: > 0 } tag
        ? tag : CultureInfo.CurrentUICulture.Name;
    private readonly IReadOnlyDictionary<string, string> messages;

    private DesktopLocale(string tag, IReadOnlyDictionary<string, string> messages)
    {
        Tag = tag;
        Culture = CultureInfo.GetCultureInfo(tag);
        this.messages = messages;
    }

    internal string Tag { get; }
    internal CultureInfo Culture { get; }
    internal static DesktopLocale Chinese => Locales[SimplifiedChinese];

    internal static string NormalizePreference(string? requested)
        => requested is not null && Locales.TryGetValue(requested, out var locale) ? locale.Tag : DesktopSettings.AutomaticLanguage;

    internal static DesktopLocale Resolve(string preference)
    {
        if (Locales.TryGetValue(preference, out var locale)) return locale;
        string tag = SystemLanguage;
        while (true)
        {
            if (Locales.TryGetValue(tag, out locale)) return locale;
            int separator = tag.LastIndexOf('-');
            if (separator < 0) break;
            tag = tag[..separator];
        }
        if (tag.Equals("zh", StringComparison.OrdinalIgnoreCase)) return Chinese;
        return Locales.Values.Where(candidate => candidate.Tag.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase))
            .MinBy(candidate => candidate.Tag, StringComparer.OrdinalIgnoreCase) ?? Locales[English];
    }

    internal string Text(string key, params (string Name, object Value)[] arguments) => Text(new DesktopText(key, arguments));

    [return: NotNullIfNotNull(nameof(message))]
    internal string? Text(DesktopText? message)
    {
        if (message is null) return null;
        string template = messages.GetValueOrDefault(message.Key) ?? Locales[English].messages.GetValueOrDefault(message.Key) ?? message.Key;
        if (message.Arguments.Length == 0) return template;
        return Placeholder.Replace(template, match => message.Arguments.FirstOrDefault(argument => argument.Name == match.Groups[1].Value).Value
            is { } value ? Format(value) : match.Value);
    }

    private string Format(object value) => value switch
    {
        long count => count.ToString("N0", Culture),
        DesktopText message => Text(message),
        DesktopText[] list => string.Join(Text("backend.listSeparator"), list.Select(Text)),
        _ => Convert.ToString(value, Culture) ?? ""
    };

    private static Dictionary<string, DesktopLocale> LoadLocales()
    {
        var assembly = typeof(DesktopLocale).Assembly;
        var locales = new Dictionary<string, DesktopLocale>(StringComparer.OrdinalIgnoreCase);
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(ResourceExtension, StringComparison.Ordinal)) continue;
            string tag = resource[ResourcePrefix.Length..^ResourceExtension.Length];
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var catalog = JsonDocument.Parse(stream);
            var messages = catalog.RootElement.GetProperty("messages").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
            locales.Add(tag, new(tag, messages));
        }
        return locales;
    }
}

internal sealed record DesktopVolumeStatus(string Root, DesktopText Text)
{
    internal bool HasError { get; init; }

    internal VolumeStatus Render(DesktopLocale locale)
        => new(Root, locale.Text(Text)) { HasError = HasError };
}
