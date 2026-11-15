using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure;

/// <summary>The languages the API answers in, bound from the <c>Localization</c> section.</summary>
internal sealed class LocalizationSettings
{
    public const string SectionName = "Localization";

    /// <summary>Language codes with a <c>Resources/Messages.&lt;code&gt;.resx</c>, plus English, which needs none.</summary>
    [MinLength(1)]
    public string[] Languages { get; set; } = ["en", "tr"];

    /// <summary>The language for callers that send no <c>Accept-Language</c> the API supports.</summary>
    [Required]
    public string DefaultLanguage { get; set; } = "en";

    internal static bool AreValid(LocalizationSettings settings) =>
        settings.Languages.All(IsCulture) && settings.Languages.Contains(settings.DefaultLanguage, StringComparer.OrdinalIgnoreCase);

    private static bool IsCulture(string name)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(name) && !CultureInfo.GetCultureInfo(name, predefinedOnly: true).Equals(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}

internal static class Localization
{
    /// <summary>
    /// Picks the language of each response from <c>Accept-Language</c>. Only the UI culture changes:
    /// the formatting culture stays invariant, so parsing, number and date formatting and
    /// case-insensitive comparisons behave the same for every caller (in Turkish, "i".ToUpper() is "İ").
    /// </summary>
    public static WebApplicationBuilder AddLocalization(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<LocalizationSettings>()
            .BindConfiguration(LocalizationSettings.SectionName)
            .ValidateDataAnnotations()
            .Validate(LocalizationSettings.AreValid, "Localization:Languages must hold culture names such as 'en' or 'tr', including Localization:DefaultLanguage.")
            .ValidateOnStart();

        // IStringLocalizer<Messages> for messages outside a response.
        builder.Services.AddLocalization();
        builder.Services.AddOptions<RequestLocalizationOptions>()
            .Configure<IOptions<LocalizationSettings>>((options, settings) =>
            {
                options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo(settings.Value.DefaultLanguage));
                options.SupportedCultures = [CultureInfo.InvariantCulture];
                options.SupportedUICultures = [.. settings.Value.Languages.Select(language => CultureInfo.GetCultureInfo(language))];
                options.FallBackToParentUICultures = true;
                // An API has no cookie or query string convention for this; clients send Accept-Language.
                options.RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()];
                // Content-Language tells the client which language it got.
                options.ApplyCurrentCultureToResponseHeaders = true;
            });

        return builder;
    }

    /// <summary>
    /// Translates what the API and the framework write in English: a problem's title and detail,
    /// and each validation error.
    /// </summary>
    public static void Translate(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        problem.Title = problem.Title is null ? null : Messages.Translate(problem.Title);
        problem.Detail = problem.Detail is null ? null : Messages.Translate(problem.Detail);
        if (problem is HttpValidationProblemDetails validation)
        {
            foreach (var field in validation.Errors.Keys.ToArray())
            {
                validation.Errors[field] = [.. validation.Errors[field].Select(Messages.Translate)];
            }
        }
    }
}
