# The `ek` tool

`ek` is a dotnet tool that generates projects from the `ek-api` template and keeps them up to date
with newer template versions. It carries the template package of its own version, so you do not
need to install the template first, and it never changes the templates installed in `dotnet new`.

```bash
dotnet tool install --global Erkmangelis.Ek --add-source <folder with Erkmangelis.Ek.0.5.0.nupkg>
ek version
```

Everything `ek` does can be done with plain `dotnet new` too. `ek` adds the questions, the presets,
clear errors for unsupported combinations, and `ek update`.

## Create a project

```bash
ek new                                       # asks for the name, a preset or each option
ek new Garage --preset saas-api -y           # a preset, no questions
ek new Garage --auth identity --db sqlite    # asks only for the options you left out
ek new Garage --preset api-worker --aspire false -y   # a preset with one option changed
```

`-y` (or input that is not a terminal) answers nothing and takes the defaults. Each question only
offers values the rules accept with your earlier answers, so the result is always a supported
combination. Before generating, `ek new` prints the equivalent `dotnet new` command:

```text
Same as: dotnet new ek-api -n Garage -o Garage --auth identity --db postgres --email smtp --jobs hangfire --worker false --aspire false --agents claude
```

Running that command gives the same files (CI checks this for every preset). Other `ek new` flags:

| Flag | Does |
| --- | --- |
| `-o <dir>` | Where to generate. Default: a folder named after the project. |
| `--spec <file>` | Read the name, preset and options from a spec file. Flags given next to it win. |
| `--save-spec <file>` | Also write the resolved spec to a file, for example to review or reuse it. |
| `--dry-run` | Print the spec and the `dotnet new` command, generate nothing. |
| `--package <nupkg>` | Generate from another template package instead of the one `ek` carries. |

## Presets

| Preset | For | Options |
| --- | --- | --- |
| `minimal` | The hardened API alone, notes in memory | everything `none`/`false` |
| `saas-api` | A product with user accounts | `--auth identity --db postgres --email smtp --jobs hangfire --agents claude` |
| `api-worker` (alias `ek-worker`) | An API plus a worker that runs its jobs | `--db postgres --jobs hangfire --worker --aspire` |
| `sso-api` | Accounts in your identity provider | `--auth oidc --db postgres` |
| `single-box` | One small machine such as a Raspberry Pi | `--auth identity --db sqlite --email smtp --jobs hosted` |

`ek list` prints the options, rules and presets. Presets live in
[catalog/presets.json](../catalog/presets.json); options given next to a preset win over its values.

## Specs

A spec is a JSON file that says what to generate. Its schema is
[schemas/ek-spec.schema.json](../schemas/ek-spec.schema.json):

```json
{
  "specVersion": 1,
  "template": "ek-api",
  "name": "Garage",
  "preset": "saas-api",
  "options": { "db": "sqlite" }
}
```

Every generated project has its full, resolved spec in `template-origin.json` (template, version,
name and every option), so `ek new --spec template-origin.json -o copy` generates the same project
again. `ek check <spec>` validates a spec and prints what it resolves to.

## Rules and unsupported combinations

The rules between options live in [catalog/modules.json](../catalog/modules.json). An option you
leave out takes the value a rule needs, or its default; a value you set that breaks a rule is
rejected, with the reason:

```text
$ ek new Garage --worker --jobs hosted -y
error: This combination is not supported:
  worker true needs jobs hangfire, but jobs is hosted. The worker runs the API's Hangfire jobs in its own process. [worker-needs-hangfire]
```

| Rule | When | Needs |
| --- | --- | --- |
| `auth-needs-database` | `--auth identity`, `external` or `oidc` | a database; PostgreSQL if you chose none |
| `hangfire-needs-database` | `--jobs hangfire` | a database; PostgreSQL if you chose none |
| `worker-needs-hangfire` | `--worker` | `--jobs hangfire`, set for you if you chose none |

Plain `dotnet new` applies the same rules through the template's computed symbols but cannot reject
anything: `dotnet new ek-api --auth identity --db none` quietly generates PostgreSQL. Tests check
that the catalog's options are the template's symbols, and that for every option value the rules
pick the same effective options the template writes to `template-origin.json`.

## Update a project

```bash
cd Garage
git status            # must be clean
ek update             # to the version ek carries
git diff              # review, then build, test and commit
```

`ek update` works like [Copier](https://copier.readthedocs.io/en/latest/updating/). It reads
`template-origin.json`, generates the project again at the version it came from and at the new
version (with the same name and options), and applies the difference to your project file by file:

- a file you never changed takes the new version, and a new template file is added;
- a file you changed and the template changed is merged line by line; where both changed the same
  lines it gets conflict markers (`<<<<<<< your project` ... `>>>>>>> template 0.5.0`), and
  `ek update` exits with 1 and lists them;
- a file you changed that the template removed, and a file you deleted that the template changed,
  are left as they are and listed for you to check;
- files the template does not know, such as your own features, are never touched.

User secrets ids are kept: every generation gets fresh GUIDs, and `ek update` aligns them with your
project's before comparing.

Options added since your version take their defaults; `template-origin.json` then records them, so
the next update knows them. Projects from before 0.5 do not record their name; `ek update` reads it
from the solution file.

| Flag | Does |
| --- | --- |
| `--to <version or nupkg>` | The version to update to. Default: the version `ek` carries. |
| `--from <version or nupkg>` | The version the project came from. Default: `template-origin.json`. |
| `--source <dir>` | A folder of template packages to find versions in. Can be repeated. |
| `--dry-run` | List what would change, write nothing. |
| `--allow-dirty` | Run on a working tree with uncommitted changes, or outside git. |

Versions are looked up in the `--source` folders, then `~/.ek/packages`, then downloaded from the
repository's GitHub releases into that cache. While the repository is private the download fails;
pass `--from` with the old `.nupkg` (from the release page) or a `--source` folder holding it.

CI generates projects with released versions (0.1.0, 0.3.0 and 0.4.0), adds a file of the team's
own, runs `ek update` to the current version and runs the full acceptance test on the result.

## Maintainers

- `ek schema` prints the spec schema generated from the catalog. A test fails when
  `schemas/ek-spec.schema.json` is stale: `dotnet run --project src/Erkmangelis.Ek -- schema > schemas/ek-spec.schema.json`.
- `ek matrix` prints the CI acceptance matrix: every preset, plus combinations that cover every pair
  of option values the rules allow together. CI reads it, so a new option or value is tested
  without editing the workflow.
- A new template option needs a symbol in `template.json`, an entry in `catalog/modules.json` (with
  any rules), a line in `template-origin.json`, a regenerated schema and, where it changes code,
  snapshot tests.
