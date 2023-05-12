# CiderPress II Download and Installation Guide #

The command-line and GUI applications are written in C#, targeted to .NET Core 6.  The .NET
runtime is available for download from Microsoft for a variety of platforms, including Windows,
Mac OS, and Linux.  It's even available for the
[Raspberry PI](https://learn.microsoft.com/en-us/dotnet/iot/deployment).

Not everyone wants to install the .NET runtime on their system, so CiderPress II is available
as both "framework-dependent" and "self-contained" downloads.  The former is much smaller
(by ~30MB), but the latter will work without having the runtime installed.

If you want to install the runtime, get it here: https://dotnet.microsoft.com/en-us/download .
Click on "all .NET 6.0 downloads" (or whatever version is current), then in the
".NET Runtime" section (or, for Windows, ".NET Desktop Runtime" section), download the
appropriate item from the Installers column.  (You can also download a more complete package,
such as the SDK or the ASP.NET Core Runtime, but they're larger.  The downloads in the Binaries
column will give you a "dotnet" executable and libraries without an installer, which means you'll
have the runtime but may not have it automatically configured in your shell path.)

You can pick a download from the [Releases page](https://github.com/fadden/ciderpress2/releases),
or use one of these links to download a recent release:

System      | Self-Cont'd? | Link
----------- | ------------ | ----
Windows x86 | no           | https://github.com/fadden/CiderPress2/releases/download/v0.1.0-dev1/cp2_0.1.0-d1_win-x86_fd.zip
Windows x86 | yes          | https://github.com/fadden/CiderPress2/releases/download/v0.1.0-dev1/cp2_0.1.0-d1_win-x86_sc.zip
Windows x64 | no           | https://github.com/fadden/CiderPress2/releases/download/v0.1.0-dev1/cp2_0.1.0-d1_win-x64_fd.zip
Windows x64 | yes          | https://github.com/fadden/CiderPress2/releases/download/v0.1.0-dev1/cp2_0.1.0-d1_win-x64_sc.zip
Mac OS x64  | no           | https://github.com/fadden/CiderPress2/releases/download/v0.1.0-dev1/cp2_0.1.0-d1_osx-x64_fd.zip
Mac OS x64  | yes          | https://github.com/fadden/CiderPress2/releases/download/v0.1.0-dev1/cp2_0.1.0-d1_osx-x64_sc.zip
Linux x64   | no           | https://github.com/fadden/CiderPress2/releases/download/v0.1.0-dev1/cp2_0.1.0-d1_linux-x64_fd.zip
Linux x64   | yes          | https://github.com/fadden/CiderPress2/releases/download/v0.1.0-dev1/cp2_0.1.0-d1_linux-x64_sc.zip

Once downloaded, unzip the file somewhere convenient (for Safari on the Mac, it will do the unzip
for you).  The various commands can be run directly from the download directory.  There are a couple
of additional steps for some systems; you will need a command shell to run them and to run the
commands themselves.

 - Windows: Windows+R to open "run" window, type "cmd" and hit return.  The first time you run
   the command, the Windows security system will scan it.
 - Linux: use "xterm", "gnome-terminal", or whatever you like.  You must `chmod +x cp2` to make
   it executable.
 - Mac OS: from the Finder, in the Go menu, select Utilities.  Double-click Terminal to launch it.
   You must `chmod +x cp2` to make it executable.  Then you need to remove the quarantine
   attribute from the files, or the system will not allow you to execute them.  In the directory
   where the files were unpacked, run `xattr -d comp.apple.quarantine *`.

(I hope to eliminate some of these steps in the future.)

The commands are:

 - `cp2`: command-line interface
 - `CiderPress2`: GUI interface (currently only in the framework-dependent Windows build)
 - `AddFile`: simple demo program - [README](Examples/AddFile/README.md)
 - `ListContents`: simple demo program - [README](Examples/ListContents/README.md)

On Windows, the executables will have the usual `.exe` suffix.

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
