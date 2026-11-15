# Languages

The API answers in the language the client asks for with `Accept-Language`, from the languages in
`Localization:Languages` (`en` and `tr`). Anything else gets `Localization:DefaultLanguage` (`en`).
`Content-Language` on the response says which one it got.

```http
GET /v1/notes/00000000-0000-0000-0000-000000000000
Accept-Language: tr-TR,tr;q=0.9

HTTP/1.1 404 Not Found
Content-Language: tr
{ "title": "Bulunamadı", "status": 404, ... }
```

## What is translated

Problem responses: their `title`, their `detail` and every validation error. That covers what the
framework writes ("Not Found", "The Title field is required.") and what features write
("A note with this title already exists.").

Code keeps writing English. `Localization.Translate`, run on every problem response, looks the
English text up in `src/MyProject.Api/Resources/Messages.resx` and swaps in the caller's language
from `Messages.<language>.resx`. Placeholders work like `string.Format`: the English entry
`The {0} field is required.` matches `The Title field is required.`, and the Turkish entry
`{0} alanı zorunludur.` becomes `Title alanı zorunludur.`. Text without an entry stays English.

## Add a message

1. Write the English text in your code as usual, for example
   `TypedResults.Problem(title: "Quota reached", detail: "You can keep up to {limit} files.")`.
2. Add an entry to `Messages.resx` with that exact text, using `{0}`, `{1}` where values go:
   `You can keep up to {0} files.`
3. Add the same key to every `Messages.<language>.resx` with the translation and the same
   placeholders. `MessagesTests` fails when a language misses a key or a placeholder.

For text outside a problem response (an email, a push notification), inject
`IStringLocalizer<Messages>` and read the key: `localizer["QuotaReachedDetail", limit]`.

## Add a language

Add `Resources/Messages.<code>.resx` with every key translated, add the code to
`Localization:Languages`, and add the code to `MessagesTests.Languages`.

## Only messages change

Only the UI culture follows the caller. The formatting culture stays invariant, so numbers, dates,
parsing and case-insensitive comparisons work the same for everyone. That matters most for
Turkish, where `"i".ToUpper()` is `"İ"` and `"I".ToLower()` is `"ı"`: code that compared strings in
the caller's culture would stop matching `"invoice"` with `"INVOICE"`. Still use ordinal
comparisons (`StringComparison.OrdinalIgnoreCase`) for identifiers, keys and anything a machine reads.
