# CiderPress II Download and Installation Guide #

The command-line and GUI applications are written in C#, targeted to .NET Core 6.  The .NET
runtime is available for download from Microsoft for a variety of platforms, including Windows,
Mac OS, and Linux.  It's even available for the
[Raspberry PI](https://learn.microsoft.com/en-us/dotnet/iot/deployment).

There are two types of downloads here: self-contained and framework-dependent.  The former
includes all necessary parts of the .NET runtime in the package, so there's no need to have .NET
installed on your system.  If you happen to have .NET Core 6 or later already installed, the
framework-dependent package is significantly smaller.

You can pick a file to download from the [Releases page](https://github.com/fadden/ciderpress2/releases),
or use one of the links below to download the most recent stable release.

**Self-Contained** (30-65 MB)

System        | Link
------------- | ----
Windows x86   | https://github.com/fadden/CiderPress2/releases/download/v1.0.7/cp2_1.0.7_win-x86_sc.zip
Windows x64   | https://github.com/fadden/CiderPress2/releases/download/v1.0.7/cp2_1.0.7_win-x64_sc.zip
Mac OS x64    | https://github.com/fadden/CiderPress2/releases/download/v1.0.7/cp2_1.0.7_osx-x64_sc.zip
Linux x64     | https://github.com/fadden/CiderPress2/releases/download/v1.0.7/cp2_1.0.7_linux-x64_sc.zip

**Framework-Dependent** (~1 MB)

System        | Link
------------- | ----
Windows x86   | https://github.com/fadden/CiderPress2/releases/download/v1.0.7/cp2_1.0.7_win-x86_fd.zip
Windows x64   | https://github.com/fadden/CiderPress2/releases/download/v1.0.7/cp2_1.0.7_win-x64_fd.zip
Mac OS x64    | https://github.com/fadden/CiderPress2/releases/download/v1.0.7/cp2_1.0.7_osx-x64_fd.zip
Linux x64     | https://github.com/fadden/CiderPress2/releases/download/v1.0.7/cp2_1.0.7_linux-x64_fd.zip

Once downloaded, unzip the file somewhere convenient (Safari on the Mac will do the unzip
for you).  There is no installer; the commands are executed directly from where they are unzipped.
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
   to make it executable.  The system adds a "quarantine" flag to anything downloaded from the
   Internet, so you need to do an extra step before you can execute the program.  In the directory
   where the files were unpacked, run `xattr -d com.apple.quarantine *`.  Run `./cp2 version`
   to confirm this was successful.  The x64 code requires Rosetta 2 to run on Apple Silicon macs;
   if you've never run an Intel-based program on your Mac, it won't be present, and you will see
   "bad CPU type in executable" messages.  [Apple support](https://support.apple.com/en-us/102527)
   has instructions for getting Rosetta 2 installed.

The commands are:

 - `cp2`: command-line interface
 - `CiderPress2`: desktop graphical interface (Windows builds only)

The download includes the manual for cp2, `Manual-cp2.md`, formatted for 80 columns for ease
of viewing in a terminal window.  The file is in "Markdown" format, which is perfectly readable
as a plain text file.  The manual for the GUI, and tutorials for both applications, can
be found on the [web site](https://ciderpress2.com/).

### Installing .NET ###

Should you wish to use the framework-dependent packages, you'll need to install the .NET runtime.
Get it here: https://dotnet.microsoft.com/en-us/download .  Find the "all .NET 8.0 downloads"
link (or whatever version is current) and click on it.  In the ".NET Runtime" section (or, for
Windows, ".NET Desktop Runtime" section), download the appropriate item from the Installers column.
You can also download a more complete package, such as the SDK or the ASP.NET Core Runtime, but
they're larger and have things you won't need if you just want to run CiderPress II.  The
downloads in the Binaries column will give you a "dotnet" executable and libraries without an
installer, which means you'll have the runtime but may not have it automatically configured in
your shell path.


## Tested Systems ##

The CiderPress II command-line utility was developed on x64 CPUs with:

 - Windows 10 (build 19044)
 - macOS 11.6.3 "Big Sur"; also tested on Apple silicon with 15.2 "Sequoia"
 - Linux Ubuntu 20.04.6 LTS

Compatibility is determined mostly by the .NET runtime, so more recent versions of the operating
systems are expected to work.  Older versions of the operating systems may or may not work.  For
example, the CLI application has been successfully run on Windows 7 32-bit (x86), but this will
not be tested regularly.

The code is targeted to .NET Core 6, and is expected to work on newer versions of the runtime.

## Support for Other Systems ##

The contents of the framework-dependent download packages for different systems are almost
entirely identical.  The application itself is compiled to platform-agnostic bytecode.  Each
platform has a system-specific "cp2" executable that gets the runtime up and pointed in the
right direction.  The only significant difference is between 32-bit code and 64-bit code; you
cannot run a 64-bit package on a 32-bit runtime.

If you want to run the command-line app on a platform that doesn't have a system-specific
binary on the Releases page, you need to install the .NET runtime for your system, and then
download one of the framework-dependent packages (e.g. `win-x86_fd` or `win-x64_fd` depending
on whether your target is 32-bit or 64-bit, respectively).  You can then run the program with
`dotnet cp2.dll <args>`.  For convenience, you can wrap that up in a shell script, like this:

    #!/bin/sh
    exec PATH/TO/dotnet PATH/TO/cp2.dll $@

### Using the GUI on Linux and macOS ###

It's possible to run the GUI application under macOS and Linux using the
[Wine](https://www.winehq.org/) compatibility layer.  Instructions for setting this up can be found
[here](https://github.com/fadden/CiderPress2/blob/main/WineNotes.md).  In addition, a pre-built
"wrapped" application for macOS may be available for download from the Releases page.

The emulation is very good, but not perfect.  You may experience problems that don't occur when
run natively on Windows.
