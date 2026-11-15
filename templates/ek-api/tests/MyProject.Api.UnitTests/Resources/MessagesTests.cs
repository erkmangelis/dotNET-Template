using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;

namespace MyProject.Api.UnitTests.Resources;

public sealed class MessagesTests
{
    public static TheoryData<string> Languages => ["tr"];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_has_every_message(string language)
    {
        var english = Keys(CultureInfo.InvariantCulture);
        var translated = Keys(CultureInfo.GetCultureInfo(language));

        Assert.Equal(english.Order(), translated.Order());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translations_keep_every_placeholder(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        foreach (var key in Keys(CultureInfo.InvariantCulture))
        {
            Assert.Equal(Placeholders(English(key)), Placeholders(Messages.Resources.GetString(key, culture)!));
        }
    }

    /// <summary>The framework's default messages, which <see cref="Messages.Translate"/> recognises by their English text.</summary>
    [Fact]
    public void The_validation_entries_match_the_framework_messages()
    {
        Assert.Equal(Expected("ValidationRequired", "Title"), new RequiredAttribute().FormatErrorMessage("Title"));
        Assert.Equal(Expected("ValidationStringLength", "Title", 200), new StringLengthAttribute(200).FormatErrorMessage("Title"));
        Assert.Equal(Expected("ValidationStringLengthRange", "Title", 200, 1), new StringLengthAttribute(200) { MinimumLength = 1 }.FormatErrorMessage("Title"));
        Assert.Equal(Expected("ValidationRange", "Page", 1, 100), new RangeAttribute(1, 100).FormatErrorMessage("Page"));
        Assert.Equal(Expected("ValidationEmailAddress", "Email"), new EmailAddressAttribute().FormatErrorMessage("Email"));
        Assert.Equal(Expected("ValidationUrl", "Link"), new UrlAttribute().FormatErrorMessage("Link"));
    }

    [Fact]
    public void Translate_keeps_the_values_that_filled_the_placeholders()
    {
        var translated = InCulture("tr", () => Messages.Translate("The field Page must be between 1 and 100."));

        Assert.Equal("Page alanı 1 ile 100 arasında olmalıdır.", translated);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void Text_without_an_entry_is_left_as_it_is(string language) =>
        Assert.Equal("Something only this feature says.", InCulture(language, () => Messages.Translate("Something only this feature says.")));

    [Fact]
    public void English_callers_get_the_text_unchanged() =>
        Assert.Equal("Not Found", InCulture("en-GB", () => Messages.Translate("Not Found")));

    private static HashSet<string> Keys(CultureInfo culture)
    {
        var set = Messages.Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];
    }

    private static string English(string key) =>
        Messages.Resources.GetString(key, CultureInfo.InvariantCulture) ?? throw new MissingManifestResourceException(key);

    private static string Expected(string key, params object[] values) =>
        string.Format(CultureInfo.InvariantCulture, CompositeFormat.Parse(English(key)), values);

    private static string[] Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Distinct().Order()];

    private static string InCulture(string name, Func<string> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
