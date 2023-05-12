# MakeDist #

This is a command-line tool for creating CiderPress II distributions.  It builds the various
components for multiple platforms and packages them up.

Usage: `MakeDist build [--debug|--release]`
       `MakeDist clobber`

## Build ##

The build process is performed by running `dotnet build` with various arguments.  The process
is repeated for each executable target, resulting in a collection of compiled objects.  This
is repeated for each platform (Windows, Linux, Mac OS), with separate builds for runtime-dependent
and self-contained binary sets.  Documentation and support files are copied in, and then each
collection is packaged up in a ZIP file.

https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build

Each `dotnet build` command takes a "runtime ID" or "RID" option.  This specifies which system
should be targeted when doing platform-specific things.  The RID catalog can be found in
"runtime.json" in the runtime installation.

https://learn.microsoft.com/en-us/dotnet/core/rid-catalog

The programs are currently built for win-x86, win-x64, linux-x64, and osx-x64.

Some general info: https://stackoverflow.com/q/41533592/294248

The default behavior is to build for release.  Debug builds have extra debugging info in ".pdb"
files, and are built with assertions and extended debug checks enabled.  This makes the programs
slightly larger and slower, though that won't generally be noticeable.

When the build completes, a collection of ZIP archives will be available in the DIST subdirectory.
These are ready to be shipped.

The ZIP files are named `cp2_<version-tag>_<rid>_<fd|sc>[_debug].zip`.  The `version-tag` is a
short form of the version number, obtained from AppCommon.GlobalAppVersion.  `rid` is the dotnet
runtime ID.  `fd` indicates framework-dependent, `sc` indicates self-contained.  Debug builds get
an additional `_debug`.  This naming convention allows the download files for all versions and
all RIDs to sit in the same directory, and sort nicely.

## Clobber ##

The "clobber" feature recursively removes all "obj" and "bin" directories found in the same
directory as a ".csproj" file.  This is more thorough than the Visual Studio "make clean".
This does not try to remove "MakeDist/bin", since it will likely be executing.

If Visual Studio is active, it will recreate the directory structure immediately.
