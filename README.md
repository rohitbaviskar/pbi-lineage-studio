# PBI Lineage Studio

PBI Lineage Studio is a standalone Windows application for exploring dependencies in local Power BI PBIP and TMDL projects. It turns semantic-model objects into interactive data-flow and data-model views without uploading model files or metadata.

## Features

- Loads local `.SemanticModel` projects and TMDL table definitions.
- Visualizes tables, columns, measures, relationships, partitions, and report usage.
- Traces upstream and downstream dependencies.
- Previews lineage paths on hover and supports progressively narrowed Ctrl+click highlights.
- Provides data-flow and data-model views with search and inspection.
- Exports the complete lineage canvas to PNG.
- Shows embedded release notes and supports automatic or on-demand update checks.
- Checks GitHub Releases for verified application updates.
- Runs locally as a single Windows executable.

## Run

Download or build `PBI Lineage Studio.exe`, then open it and select a Power BI semantic-model folder. You can also pass the folder at startup:

```powershell
& '.\PBI Lineage Studio.exe' 'C:\path\to\Model.SemanticModel'
```

The application reads local project files only and does not connect to an XMLA endpoint. It contacts this repository's GitHub Releases endpoint to check for application updates, but it does not upload model files or model metadata.

## Build from source

Requirements:

- Windows
- .NET Framework 4.x C# compiler (`csc.exe`), supplied by the .NET Framework Developer Pack or Visual Studio Build Tools

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The build produces `PBI Lineage Studio.exe` in the repository root.

## Prepare a release

`VERSION` is the single source for the version shown locally, in About, and in GitHub releases. It supports any number of numeric levels, such as `0.2.4`, `0.2.4.1`, or `0.2.4.1.1`. To prepare version 0.2.4.1, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\prepare-release.ps1 -Version 0.2.4.1
```

The script updates `VERSION`, rebuilds the local executable, and verifies the complete application version. Windows limits its native file-version field to four numbers, so versions with more levels retain the complete value in About, update manifests, and GitHub tags while Windows displays the first four. Review and commit all intended release changes, push the commit, and then create and push the matching tag:

```powershell
git push origin main
git tag v0.2.4.1
git push origin v0.2.4.1
```

Pushing the tag starts the GitHub Actions release workflow. The workflow stops with an error if the tag and `VERSION` do not match.

## Register as a Power BI external tool

Build the executable, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\register-powerbi-external-tool.ps1
```

Restart Power BI Desktop after registration. The script writes the external-tool manifest to your Power BI Desktop `External Tools` folder.

## Repository layout

```text
native/PbiLineageStudio.cs                 Application source
VERSION                                    Single application version source
RELEASE_NOTES.md                           Release notes embedded in the executable
scripts/build.ps1                          Reproducible Windows build
scripts/prepare-release.ps1                Local version bump and release check
scripts/register-powerbi-external-tool.ps1 Optional Power BI registration
PBI Lineage Studio.exe                     Built application
```

## Privacy

Model files and model metadata stay on the local machine. The automatic update check sends only a standard HTTPS request to GitHub Releases. Review exported screenshots before sharing them because they can contain model names, DAX expressions, and source details.

## License

Licensed under the MIT License. See [LICENSE](LICENSE).
