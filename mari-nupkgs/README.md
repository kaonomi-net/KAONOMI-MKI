[www.kaonomi.net](https://www.kaonomi.net)

# MARI packages

Local NuGet feed of the MARI packages (`MARI.*`, produced by `dotnet pack` of the MARI solution) until they are
published on nuget.org. `nuget.config` points here with a path relative to the repository, so the same feed works on
Windows, macOS, Linux and in the Docker builds.

Put the `.nupkg` files of the MARI version used (1.0.0) in this folder, or link the folder to the shared feed:

- Windows (shared feed on `N:`), from the repository root, after removing this folder:
  `mklink /J mari-nupkgs N:\mari-nupkgs\1.0.0`
- macOS / Linux (share mounted, for example on `/Volumes/mari-nupkgs`):
  `cp /Volumes/mari-nupkgs/1.0.0/*.nupkg mari-nupkgs/`

The `.nupkg` files are not committed (`*.nupkg` is in `.gitignore`). Once the packages are in the NuGet cache
(`~/.nuget/packages`), a restore no longer needs them.
