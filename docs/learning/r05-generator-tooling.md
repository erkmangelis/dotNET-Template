# R05, Generator tooling (0.5.0): why there is an `ek` tool

By R04 the template had nine options and hundreds of possible combinations, and three problems
`dotnet new` cannot solve on its own:

1. It accepts combinations that make no sense and silently adjusts them (R02's "auth turns
   PostgreSQL on" happened without telling you).
2. Nobody can remember nine options; there was no way to say "a typical SaaS API".
3. A project generated last month cannot take this month's template fixes.

R05 answers all three with a small dotnet tool, `ek`, plus a catalog of options and rules, presets,
a versioned project spec, a pairwise CI matrix and `ek update`. Nothing in the generated API
changed much; this release is about the *machinery around* the template.

## Contents

1. [A tool on top of `dotnet new`, not instead of it](#1-a-tool-on-top-of-dotnet-new-not-instead-of-it)
2. [The catalog: options and rules as data](#2-the-catalog-options-and-rules-as-data)
3. [Explicit values vs fallbacks](#3-explicit-values-vs-fallbacks)
4. [Presets](#4-presets)
5. [A versioned project spec and its JSON Schema](#5-a-versioned-project-spec-and-its-json-schema)
6. [Pairwise testing: 18 combinations instead of hundreds](#6-pairwise-testing-18-combinations-instead-of-hundreds)
7. [`ek update`: the three-way merge](#7-ek-update-the-three-way-merge)
8. [The tool carries its own template](#8-the-tool-carries-its-own-template)
9. [Tests that keep the catalog honest](#9-tests-that-keep-the-catalog-honest)

---

## 1. A tool on top of `dotnet new`, not instead of it

**The choice.** `ek` is a dotnet tool (`dotnet tool install Erkmangelis.Ek`) that asks questions,
resolves the options, prints the equivalent `dotnet new` command and then *runs* `dotnet new`.

```text
Same as: dotnet new ek-api -n Garage -o Garage --auth identity --db postgres --email smtp ...
```

**Why not replace `dotnet new`.** Everything `ek` produces can be reproduced with the printed
command, and CI checks that the two give byte-identical output for every preset. So `ek` adds
convenience and safety without becoming a lock-in: if `ek` disappeared tomorrow, the template still
works.

**Why a dotnet tool.** The audience already has the .NET SDK. A dotnet tool installs with one
command on every OS and is versioned through NuGet like everything else. A Node or Python CLI would
need another runtime.

**Why `ek new` runs in a private template hive.** `dotnet new` keeps installed templates in a
per-user "hive". `ek` uses `--debug:custom-hive` with its own folder, so it never installs,
upgrades or removes templates in *your* `dotnet new`. Generating at an old version for `ek update`
(section 7) needs this too.

**Alternatives.** Spring Initializr and the Angular CLI are the models here: a guided front-end to
a generator. Copier (Python) inspired `ek update`. Yeoman (Node) was the older equivalent.

## 2. The catalog: options and rules as data

```jsonc
// catalog/modules.json (one rule)
{
  "id": "auth-needs-database",
  "when":     { "auth": ["identity", "external", "oidc"] },
  "requires": { "db": ["postgres", "sqlserver", "sqlite"] },
  "fallback": { "db": "postgres" },
  "reason": "Every sign-in mode stores users' data in a database."
}
```

**The choice.** Options, their questions and descriptions, and the rules between them live in a
JSON file, not in C# code.

**Why data.** Several consumers need the same knowledge: the interactive prompts (offer only valid
values), the resolver (reject invalid combinations), `ek list`, the JSON Schema, the CI matrix,
and the docs. In code, each would drift. As data, there is one source of truth, and a future
website (R13) can read the same file.

**Why every rule has a `reason`.** When `ek` rejects `--worker true --jobs hosted`, it prints the
rule's reason ("The worker runs the API's Hangfire jobs in its own process."), not just "invalid". An error that explains itself
teaches the user the system.

**`requires`, `excludes`, `fallback`.** These three cover every rule so far: "this needs one of
those", "this cannot go with that", and "if you did not choose, pick this".

## 3. Explicit values vs fallbacks

```csharp
// src/Erkmangelis.Ek/SpecResolver.cs
/// Applies a preset, the explicit options and the catalog rules. An option nobody set takes the
/// value a rule needs, or else its default; an explicit value that breaks a rule is rejected with
/// the rule's reason. Preset values count as explicit, so a preset is never silently changed.
```

The key idea is the difference between "you said" and "you did not say":

- `ek new --auth identity` (db not mentioned): the rule fills in `db = postgres` and tells you.
- `ek new --auth identity --db none` (you said none): rejected, with the reason.

**Why not silently fix it like `dotnet new` does.** If you explicitly asked for no database and got
one, you would not notice until you saw a PostgreSQL container in compose. Rejecting a contradiction
is friendlier than overriding it.

**Why preset values count as explicit.** Otherwise a preset could be silently changed by a rule,
and "saas-api" would mean different things depending on other flags.

**`dotnet new` keeps working the old way** (computed symbols adjust), because the template engine
has no way to reject. Tests check that the catalog's rules and the template's computed symbols
give the same effective options for every combination, so the two never disagree.

## 4. Presets

```jsonc
// catalog/presets.json
{ "name": "single-box",
  "description": "Everything on one small machine such as a Raspberry Pi: Identity accounts on SQLite, in-process jobs ...",
  "options": { "auth": "identity", "db": "sqlite", "email": "smtp", "jobs": "hosted", ... } }
```

**Why presets.** Most people want one of a few shapes: a minimal API, a SaaS backend, an API plus a
worker, an SSO-protected API, everything on one box. A preset names that shape and picks all nine
options. You can still override one: `ek new Garage --preset api-worker --aspire false`.

**Why every preset sets every option.** A preset that leaves options unset would change meaning
when a new option with a different default is added. Spelling everything out makes each preset
stable.

**`ek-worker` is an alias, not a template.** R04 decided against a second template. A preset alias
gives people the name they look for without duplicating the template.

## 5. A versioned project spec and its JSON Schema

```json
{ "specVersion": 1, "template": "ek-api", "name": "Garage", "preset": "saas-api", "options": { "db": "sqlite" } }
```

**Why a spec file.** It lets you write down what to generate, review it in a pull request, and
regenerate later (`ek new --spec garage.json`). Every generated project's `template-origin.json`
(from R01) became a full spec in R05, so any project can be regenerated exactly.

**Why `specVersion`.** The format will change. A version number lets future `ek` versions read old
specs correctly instead of guessing.

**Why a JSON Schema, generated from the catalog.** Editors (VS Code, Rider) use the schema for
autocompletion and validation while you type a spec. It is generated by `ek schema`, and a test
fails if the committed schema is stale, so it cannot drift from the catalog.

## 6. Pairwise testing: 18 combinations instead of hundreds

R01's rule is "supported means tested". With nine options, testing every combination in CI was
impossible (each acceptance run takes minutes and some need Docker Compose). R04 had grown to 32
hand-picked combinations, and picking by hand does not scale.

**The idea.** Most bugs come from one option alone or from **two options interacting** (SQLite +
Hangfire, OIDC + API keys). Bugs that need three specific values at once are much rarer. So test a
set of combinations in which **every pair of values appears at least once**. That is *pairwise* (or
all-pairs) testing, a standard technique in combinatorial testing.

```csharp
// src/Erkmangelis.Ek/TestMatrix.cs (trimmed)
var candidates = SpecResolver.SupportedCombinations(catalog).ToList();
var uncovered = candidates.SelectMany(combination => Pairs(names, combination)).ToHashSet();
uncovered.ExceptWith(covered.SelectMany(combination => Pairs(names, combination)));   // presets first
while (uncovered.Count > 0)
{
    // The first candidate covering the most uncovered pairs; ties keep catalog order.
    var best = candidates.MaxBy(combination => Pairs(names, combination).Count(uncovered.Contains))!;
    uncovered.ExceptWith(Pairs(names, best));
    chosen.Add(best);
}
```

**How it works.** Start with every preset (those are what people actually use). List every pair of
option values that can occur in a supported combination. Repeatedly pick the supported combination
that covers the most still-uncovered pairs. This *greedy* algorithm is not guaranteed to find the
smallest set, but it is close, simple to read and **deterministic**, so the matrix only changes when
the catalog changes.

**Only supported combinations are candidates**, so the rules are respected automatically.

**CI reads the matrix from the tool.** `ek matrix --github` prints the GitHub Actions matrix. Add
an option to the catalog and CI covers it without editing the workflow.

**Trade-off.** A bug that needs three values together can slip through. Presets cover the most
common triples, and snapshot tests still check the output of many more combinations cheaply.

## 7. `ek update`: the three-way merge

This is the most interesting algorithm in the project, and it comes from Copier (a Python project
generator) and from how `git merge` works.

**The problem.** You generated a project at 0.3.0 and changed it a lot. The template is now at
0.5.0 with fixes. You cannot regenerate (that would erase your changes), and diffing your project
against 0.5.0 mixes your changes with the template's.

**The insight.** You need *three* versions of each file:

| Name | What it is |
| --- | --- |
| **base** | the file as 0.3.0 generated it, with your options |
| **ours** | the file in your project today |
| **theirs** | the file as 0.5.0 would generate it, with the same options |

`ek update` produces *base* and *theirs* by generating the project twice, in private hives, at the
old and the new version (that is why `template-origin.json` records the version and options). Then,
per file:

```csharp
// src/Erkmangelis.Ek/ThreeWayMerge.cs (trimmed)
if (Same(old?.Content, @new?.Content)) continue;              // template did not change it: nothing to do
var current = project(path);
if (Same(current, old?.Content))      -> take the new version  // you never touched it: Updated / Added / Removed
else if (Same(current, @new?.Content)) -> nothing              // you already have the new version
else if (current is null)             -> StaysDeleted          // you deleted a file the template changed
else                                  -> line merge             // both changed it: Merged or Conflict
```

**The line merge uses `git merge-file`.** It is the same algorithm `git merge` uses on a file,
battle-tested for decades. Writing our own diff3 would be a source of bugs. Conflicts get the usual
markers, labelled "your project", "template 0.3.0" and "template 0.5.0".

**Safety rules.**

- **Nothing you changed is ever overwritten.** The worst case is a conflict marker you resolve.
- **A clean git working tree is required**, so `git diff` shows exactly what the update did and
  `git checkout .` undoes it.
- **User secrets ids are preserved.** Each generation creates a fresh random `UserSecretsId` GUID,
  which would otherwise show up as a change in every project file. `ek update` aligns them to your
  project's.
- **Old projects without a recorded name** (before 0.5) get it from the `.slnx` file.

**How it is tested.** CI generates projects with released versions (0.1.0, 0.3.0, 0.4.0, and 0.5.0
since R06), commits a file "of the team's own", runs `ek update` to the current version and runs the
**full acceptance test** on the result. Updating is only "supported" if the updated project builds
and passes every test.

**Alternatives.** Regenerate and diff by hand (what most teams do, and why most templated projects
never get updates), or a framework you upgrade as a package (ABP). The three-way merge keeps the
"you own plain code" benefit of a template and still lets fixes flow.

## 8. The tool carries its own template

**The choice.** The `ek` package contains the template `.nupkg` of the same version. `ek 0.5.0`
always generates with template 0.5.0.

**Why.** If the tool downloaded "the latest template", the same `ek new` command would give
different results on different days. Bundling makes the tool's version the only version you need to
know, and makes generation work offline.

**How.** An MSBuild target in `src/Erkmangelis.Ek/Erkmangelis.Ek.csproj` runs `dotnet pack` on the
template project before packing the tool and includes the result. `NoDefaultExcludes` is needed
because NuGet otherwise drops `.nupkg` files inside a package (warning NU5119). `EkBundleTemplate=false`
skips it for quick local builds.

## 9. Tests that keep the catalog honest

The catalog duplicates information that also lives in `template.json` (option names, values,
defaults, the computed rules). Duplication is dangerous unless tests check it. The tool tests
(`tests/Erkmangelis.Ek.Tests`) check that:

- every option and value in the catalog exists in `template.json` and the other way round,
- the catalog's rules give the same effective options as the template's computed symbols,
- the committed JSON Schema equals what `ek schema` generates,
- `ek new` with each preset produces the same files as the printed `dotnet new` command,
- the merge handles each change kind (unit tests on `ThreeWayMerge.Plan`).

So adding an option is a checklist the tests enforce: template symbol, catalog entry,
`template-origin.json` line, regenerated schema. The README's "Working on the template" section
lists it.

---

## Try it

1. `ek new Demo --auth identity --db none`. Read the error. Then `ek new Demo --auth identity` and
   see the fallback reported.
2. `ek matrix` and count the rows. Add a value to an option in the catalog and run it again.
3. Generate a project with an older package (`ek new --package <old nupkg>`), commit it, change one
   file, then run `ek update` and read `git diff`.
4. Read `ThreeWayMerge.Plan` and draw the decision tree for one file on paper.

## Further reading

- Copier's update model: https://copier.readthedocs.io/en/stable/updating/
- `git merge-file`: https://git-scm.com/docs/git-merge-file
- Pairwise testing: https://www.pairwise.org/ and NIST's combinatorial testing work: https://csrc.nist.gov/projects/automated-combinatorial-testing-for-software
- .NET tools: https://learn.microsoft.com/dotnet/core/tools/global-tools
