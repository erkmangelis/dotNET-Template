using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace MyProject.Api.Resources;

/// <summary>
/// The messages in <c>Messages.resx</c> (English) and <c>Messages.&lt;language&gt;.resx</c>. Code and the
/// framework write English; <see cref="Translate"/> turns a known English text into the caller's
/// language, so features do not have to look messages up themselves. Also usable as
/// <c>IStringLocalizer&lt;Messages&gt;</c> for text that is not in a response, such as an email.
/// </summary>
internal sealed class Messages
{
    internal static readonly ResourceManager Resources = new(typeof(Messages).FullName!, typeof(Messages).Assembly);

    private static readonly Lazy<Pattern[]> _patterns = new(LoadPatterns);

    private Messages()
    {
    }

    /// <summary>
    /// <paramref name="text"/> in <see cref="CultureInfo.CurrentUICulture"/> when it matches an English
    /// message, with the values that filled its placeholders kept; otherwise <paramref name="text"/> as it is.
    /// </summary>
    public static string Translate(string text)
    {
        var culture = CultureInfo.CurrentUICulture;
        if (culture.TwoLetterISOLanguageName == "en" || culture.Equals(CultureInfo.InvariantCulture))
        {
            return text;
        }

        foreach (var pattern in _patterns.Value)
        {
            var match = pattern.English.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var translated = Resources.GetString(pattern.Key, culture);
            if (translated is null)
            {
                return text;
            }

            var values = new object[pattern.Placeholders.Length == 0 ? 0 : pattern.Placeholders.Max() + 1];
            for (var index = 0; index < pattern.Placeholders.Length; index++)
            {
                values[pattern.Placeholders[index]] = match.Groups[index + 1].Value;
            }

            return string.Format(culture, translated, values);
        }

        return text;
    }

    /// <summary>
    /// One regular expression per English message: the text with each <c>{n}</c> placeholder turned
    /// into a capture, longest messages first so a short one never matches inside a longer one.
    /// </summary>
    private static Pattern[] LoadPatterns()
    {
        var english = Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException("Messages.resx is missing from the assembly.");

        var patterns = new List<Pattern>();
        foreach (DictionaryEntry entry in english)
        {
            var text = (string)entry.Value!;
            var placeholders = new List<int>();
            var regex = Regex.Replace(Regex.Escape(text), @"\\\{(\d+)}", match =>
            {
                placeholders.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                return "(.+?)";
            });
            patterns.Add(new Pattern(
                (string)entry.Key,
                text.Length,
                new Regex($"^{regex}$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100)),
                [.. placeholders]));
        }

        return [.. patterns.OrderByDescending(pattern => pattern.Length)];
    }

    private sealed record Pattern(string Key, int Length, Regex English, int[] Placeholders);
}
