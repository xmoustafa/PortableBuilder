# PortableBuilder V1.1

Windows desktop tool for detecting installed applications, reviewing their AppData/Registry paths, and building portable, click-and-run packages.

**Developed by : Moustafa**

## Downloads

Download the latest `PortableBuilder.exe` from the **Releases** page.  
The executable is self-contained; no installer or CLI is required.

## Auto-update

The app checks GitHub Releases for a newer version (at most once every 6 hours), verifies the release SHA-256, then replaces itself and relaunches. The first build that contains the updater must be downloaded manually; every later release updates automatically.

## Build from source

Requirements: Windows + .NET Framework 4.8.

```bat
cd src
build.cmd
```

## Release

Push a version tag; GitHub Actions builds and publishes the EXE + SHA-256:

```bat
git tag v1.1.1
git push origin v1.1.1
```

The release workflow injects the repository owner/name into the updater automatically.

## Documentation

Full Arabic documentation: [`src/README.md`](src/README.md)

## Notes

Use this tool only with software you are allowed to package or move. It is not a cracking or redistribution tool for paid software.
