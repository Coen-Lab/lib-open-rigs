# open-rigs

> **Coen-Lab/lib-open-rigs** is the Coen Lab's fork: its `main` is what every lab workflow runs. See [`COEN-LAB.md`](COEN-LAB.md).

Repository for the open-rigs ecosystem: shared rig schemas (Python), Bonsai acquisition operators (.NET), and a Copier template used to scaffold new experiment repositories.

Full documentation, including tutorials and API reference, is published at **https://open-rigs.github.io/open-rigs** (deployed on release).

## Prerequisites

| Tool | Purpose |
|------|---------|
| [Git](https://git-scm.com/) | Version control |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Building the C# packages and the DocFX site |
| [Python 3.11+](https://www.python.org/downloads/) with [`uv`](https://docs.astral.sh/uv/) | Python package, schema generation, tests |
| [Bonsai](https://bonsai-rx.org/) | Optional, only needed to render workflow SVGs locally |

## Build the docs locally

```bash
dotnet tool restore
dotnet docfx docs/docfx.json --serve
```

DocFX will print a `http://localhost:8080`-style URL. Open it to preview the site.

To also render workflow images (requires Bonsai on Windows), run `pwsh docs/build.ps1` instead.

## Python workflow

```bash
uv sync
uv run pytest
```
