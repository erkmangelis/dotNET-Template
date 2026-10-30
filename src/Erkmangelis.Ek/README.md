# ek

The command-line tool for the [Erkmangelis .NET templates](https://github.com/erkmangelis/dotNET-Template).

```bash
dotnet tool install --global Erkmangelis.Ek --add-source <folder with the .nupkg>
ek new Acme.Notes                          # asks a question per option
ek new Acme.Jobs --preset api-worker -y    # a preset, no questions
ek update                                  # in a generated project: take the newest template's changes
```

See [docs/cli.md](https://github.com/erkmangelis/dotNET-Template/blob/main/docs/cli.md).
