# Security scanning

Three checks run on every push to `main`, every pull request, and every Monday (new advisories
appear without any change in the code):

| Check | Workflow | Finds | Costs |
| --- | --- | --- | --- |
| Vulnerable packages | `security.yml` | NuGet packages, direct or transitive, with a published advisory | free |
| Secrets | `security.yml` | Keys, tokens and passwords in any commit of the history ([gitleaks](https://github.com/gitleaks/gitleaks)) | free |
| CodeQL | `codeql.yml` | Injection, path traversal, weak crypto and similar flaws in C# and the workflows | free for public repositories; a private one needs GitHub Code Security |

The build itself also fails on a vulnerable package: NuGet audit, on by default, turns advisories into warnings
and `-warnaserror` into errors.

## A vulnerable package

```bash
dotnet package list --vulnerable --include-transitive
```

Raise the package's version in `Directory.Packages.props`. For a transitive package (one you do not
reference), add it there with a fixed version and a comment saying why; remove that pin once the
package that pulls it in has caught up.

## A secret was found

Treat it as leaked even if the commit is old or the repository is private: **revoke or rotate it
first**, then remove it from the code (move it to user secrets or `.env`). Rewriting history does
not un-leak it, since clones and forks keep it.

Run the scan yourself before pushing:

```bash
docker run --rm -v "$PWD:/repo" zricethezav/gitleaks:v8.30.1 git /repo --config /repo/.gitleaks.toml --redact
```

A false positive (an example value, a test fixture) goes into the allowlist in `.gitleaks.toml`,
as narrowly as possible: the exact value, or one path.

## CodeQL

Findings appear under the repository's Security tab, Code scanning. Fix the code, or dismiss a
finding there with a reason when it is a false positive. The `security-extended` query suite finds
more than the default and reports a few more false positives.

On a private repository without GitHub Code Security, the CodeQL workflow fails at the upload step.
Delete `codeql.yml` in that case; the other two checks need no plan.

## Also turn on

In the repository's Settings, Security (free for public repositories):

- **Dependabot alerts and security updates**, which open pull requests for vulnerable packages.
- **Secret scanning and push protection**, which blocks a push that contains a known provider's key
  before it reaches GitHub.
- **Private vulnerability reporting**, so people can tell you about a flaw without a public issue.
