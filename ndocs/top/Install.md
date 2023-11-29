# CiderPress II Download and Installation Guide #

The command-line and GUI applications are written in C#, targeted to .NET Core 6.  The .NET
runtime is available for download from Microsoft for a variety of platforms, including Windows,
Mac OS, and Linux.  It's even available for the
[Raspberry PI](https://learn.microsoft.com/en-us/dotnet/iot/deployment).

Not everyone wants to install the .NET runtime on their system, so CiderPress II is available
as both "framework-dependent" and "self-contained" downloads.  The former is much smaller
(by 30+MB), but the latter will work without having the runtime installed.  Windows users
will likely already have the runtime installed.

If you want to install the .NET runtime, get it here: https://dotnet.microsoft.com/en-us/download .
Click on "all .NET 6.0 downloads" (or whatever version is current), then in the
".NET Runtime" section (or, for Windows, ".NET Desktop Runtime" section), download the
appropriate item from the Installers column.  You can also download a more complete package,
such as the SDK or the ASP.NET Core Runtime, but they're larger and have things you won't need
if you just want to run CiderPress II.  The downloads in the Binaries column will give you a
"dotnet" executable and libraries without an installer, which means you'll have the runtime
but may not have it automatically configured in your shell path.

You can pick a file to download from the [Releases page](https://github.com/fadden/ciderpress2/releases),
or use one of these links to download a recent release:

System      | Self-Cont'd? | Link
----------- | ------------ | ----
Windows x86 | no           | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_win-x86_fd.zip
Windows x86 | yes          | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_win-x86_sc.zip
Windows x64 | no           | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_win-x64_fd.zip
Windows x64 | yes          | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_win-x64_sc.zip
Mac OS x64  | no           | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_osx-x64_fd.zip
Mac OS x64  | yes          | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_osx-x64_sc.zip
Linux x64   | no           | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_linux-x64_fd.zip
Linux x64   | yes          | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_linux-x64_sc.zip

Once downloaded, unzip the file somewhere convenient (Safari on the Mac will do the unzip
for you).  There is no installer; the commands are executed directly from where they were unzipped.
On some systems an additional step may be necessary.  You will need a command-line shell to
run "cp2":

 - Windows: hit Windows+R to open the "run" window.  Enter "cmd" for a classic DOS shell or
   "powershell" for something fancier, and hit return.
 - Linux: use "xterm", "gnome-terminal", or whatever you like.
 - Mac OS: from the Finder, in the Go menu, select Utilities.  Double-click Terminal to launch it.

In the shell, change to the directory where the files were unzipped.  Then:

 - Windows: run `./cp2 version` to confirm it works.  You will probably need to click through some
   security warnings.  If you're only interested in the GUI version, just double-click
   `CiderPress2.exe`.
 - Linux: the command should have been made executable when unzipped; if not, use `chmod +x cp2`
   to fix it.  Run `./cp2 version` to confirm it works.
 - Mac OS: the command should have been made executable when unzipped; if not, use `chmod +x cp2`
   to make it executable.  You then need to remove the quarantine flag from the files, or the
   system will not allow you to execute them.  In the directory where the files were unpacked, run
   `xattr -d comp.apple.quarantine *`.  Run `./cp2 version` to confirm this was successful.

The commands are:

 - `cp2`: command-line interface
 - `CiderPress2`: desktop graphical interface (Windows builds only)

The download includes the manual for cp2, `Manual-cp2.md`, formatted for 80 columns for ease
of viewing in a terminal window.  The file is in "markdown" format, which is perfectly readable
as a plain text file.  The download also includes release notes for CiderPress2,
`CiderPress2-notes.txt`, which can be opened in a web browser by hitting F1 from the GUI.

## Tested Systems ##

The CiderPress II command-line utility was developed on x64 CPUs with:

 - Windows 10 (build 19044)
 - macOS 11.6.3 "Big Sur"
 - Linux Ubuntu 20.04.6 LTS

Compatibility is determined mostly by the .NET runtime, so more recent versions of the operating
systems are expected to work.  Older versions of the operating systems may or may not work.  For
example, the CLI application has been successfully run on Windows 7 32-bit, but this will not be
tested regularly.

The code is targeted to .NET Core 6, and is expected to work on newer versions of the runtime.

## Support for Other Systems ##

The contents of the framework-dependent download packages for different systems are almost
entirely identical.  The application itself is compiled to platform-agnostic bytecode.  Each
platform has a system-specific "cp2" executable that gets the runtime up and pointed in the
right direction.  The only significant difference is between 32-bit code and 64-bit code; you
cannot run a 64-bit package on a 32-bit system.

If you want to run the command-line app on a platform that doesn't have a system-specific
binary on the Releases page, you need to install the .NET runtime for your system, and then
download one of the framework-dependent packages (e.g. `win-x86_fd` or `win-x64_fd` depending
on whether your target is 32-bit or 64-bit, respectively).  You can then run the program with
`dotnet cp2.dll <args>`.  For convenience, you can wrap that up in a shell script, like this:

    #!/bin/sh
    exec PATH/TO/dotnet PATH/TO/cp2.dll $@
