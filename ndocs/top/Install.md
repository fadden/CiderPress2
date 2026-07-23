# CiderPress II Download and Installation Guide #

The command-line and GUI applications are written in C#, targeted to .NET 10.  Officially, .NET 10
is supported on Windows 10+, macOS 15+, and recent versions of Linux, but in practice it works
on older versions, including Windows 7 and macOS 11.  The downloads are are completely
self-contained, and do not require you to have .NET installed on your system.

You can pick a file to download from the [Releases page](https://github.com/fadden/CiderPress2/releases),
or use one of the links below to download the most recent stable release.

System       | Link
------------ | ----
Windows x86  | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_win-x86.zip
Windows x64  | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_win-x64.zip
macOS x64    | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_osx-x64.zip
macOS ARM64  | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_osx-arm64.zip
Linux x64    | https://github.com/fadden/CiderPress2/releases/download/v${VERSION}/cp2_${PKG_VERSION}_linux-x64.zip

Once downloaded, unzip the file somewhere convenient.  There is no installer; the commands are
executed directly from where they are unzipped.  The graphical UI tools can be launched by
double-clicking on them.  On some systems, an additional step may be necessary to complete the
installation.  If so, you will need a command-line shell:

 - Windows: hit Windows+R to open the "run" window.  Enter `cmd` for a classic DOS shell or
   `powershell` for something fancier, and hit return.
 - Linux: use `xterm`, `gnome-terminal`, or whatever you like.
 - macOS: from the Finder, in the Go menu, select Utilities.  Double-click Terminal to launch it.

In the shell, change to the directory where the files were unzipped.  Then:

 - Windows: no additional steps required.  Run `./cp2 version` to confirm it works.  You will
   probably need to click through some security warnings.
 - Linux: the command should have been made executable when unzipped; if not, use
   `chmod +x cp2 CiderPress2` to fix it.  Run `./cp2 version` to confirm it works.
 - macOS: the commands should have been marked executable when unzipped.  If you're getting an
   error message that says the program isn't working, this probably didn't happen.  Fix it with
   `chmod +x CiderPress\ II.app/Contents/MacOS/cp2` and
   `chmod +x CiderPress\ II.app/Contents/MacOS/CiderPress2`.  In addition, the system adds a
   "quarantine" flag to anything downloaded from the Internet, so you need  do an extra step
   before you can execute the program (if you don't do this, you will get a message that says
   the program is damaged and should be deleted).  From a Terminal window, in the directory where
   the files were unpacked, run `xattr -dr com.apple.quarantine CiderPress\ II.app`.  To confirm
   it worked, run `./CiderPress\ II.app/Contents/MacOS/cp2 version`.

The commands are:

 - `cp2`: command-line interface
 - `CiderPress2`: desktop graphical interface
 - `CiderPress2_wpf`: older desktop graphical interface for Windows (will be going away)

The download includes the manual for the cp2 command-line utility, `Manual-cp2.md`, formatted for
80 columns for ease of viewing in a terminal window.  The file is in "Markdown" format, which is
perfectly readable as a plain text file.  The manuals and tutorials for both sets of tools can be
found on the [web site](https://ciderpress2.com/).

## Building For Other Systems ##

If you would like to build the "cp2" utility from scratch, the steps for doing so are:

 1. Install the .NET 10 SDK for your system.  If you're using a package manager, look for
    a package with "dotnet-sdk" in the name.
 2. Clone the source tree: `git clone https://github.com/fadden/CiderPress2`.
 3. From the root directory of the source tree, run the `mkcp2.sh` shell script.
