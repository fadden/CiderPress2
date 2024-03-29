# CiderPress II Source Code Notes #

All of the code is written in C# .NET, using the (free to download) Visual
Studio Community 2022 IDE as the primary development environment.  All of
the code targets .NET Core 6.  With the exception of the WPF application,
none of the code has a machine-specific target.  The projects can be built
for a 32-bit or 64-bit environment.

When installing Visual Studio, be sure to include ".NET Desktop Development".

See [MakeDist](MakeDist/README.md) for build and packaging.  You will need a
full .NET SDK installation to do builds (with `dotnet build`).

The source files that deal with disk and file formats have accompanying
"-notes" documents that describes the format in some detail, and has
references to primary sources.

## Projects ##

Libraries:

 - CommonUtil: a library of handy functions.  This library does not depend
   on any others.
 - DiskArc: all code for managing disk images and file archives.  Depends
   on CommonUtil.
 - DiskArcTests: a collection of tests that exercise the DiskArc library.
   This is loaded at runtime by the test harness.
 - FileConv: file format converters.  Depends on DiskArc and CommonUtil.
 - FileConvTests: tests that exercise the file format converters.  This is
   loaded at runtime by the test harness.
 - AppCommon: common code for command-line and GUI applications.  Depends
   on CommonUtil, DiskArc, and FileConv.

Applications:

 - cp2: command-line application for Windows, Linux, Mac, et.al.
 - cp2_wpf: GUI application for Windows.

Other (not included in binary distribution):

 - Examples/*: some simple command-line applications that demonstrate
   the use of the libraries.
 - MakeDist: build utility that creates distribution packages.
 - TestData: a collection of file archives and disk images, used by the
   DiskArc library tests and the cp2 command-line application tests.

## Tests ##

The tests in the DiskArcTests and FileConvTests libraries can be run in two
different ways:

 1. `cp2 debug-libtest-da` and `cp2 debug-libtest-fc`
 2. From the hidden "DEBUG" menu in the cp2_wpf application.

The cp2 command-line application has its own set of tests that can be run with
`cp2 debug-test`.  See the [cp2 test README](cp2/Tests/README.md).

All tests make use of the test files stored in the TestData directory.

The hidden `--debug` option for cp2 will show additional debugging information
for certain operations.  For example, `cp2 help --debug` also lists the
commands used to run the tests, and `cp2 version --debug` will display
additional information about the runtime environment.

## GUI Tool Development ##

During development, the command-line interface was developed first.  The
graphical interface is currently being prototyped with WPF (Windows
Presentation Foundation).  This might seem an odd choice, since WPF is several
years old and Windows-only, and there are multi-platform UI toolkits. The
motivations are:

 1. Some multi-platform UI toolkits don't work on all of my target platforms.
    For example, Microsoft's MAUI doesn't currently work on Linux.
 2. Some UI toolkits emphasize mobile device interaction, making them less
    suitable for what is intended as a desktop application.
 3. Some UI toolkits are missing important pieces, like robust display of
    formatted text.
 4. I'm familiar with WPF.

My goal with the WPF implementation was to provide a useful program that
proves out the API in the underlying libraries.  The bulk of the
implementation is shared between the CLI and GUI apps, but you can't trust an
API until it has been used for a real application.  I had initially planned to
use a platform-independent UI toolkit for the first version, but I wasn't able
to find one that seemed both appropriate and ready.  WPF has a lot of issues,
but I've already fought those battles and know how to solve the problems.
Learning a new API and working through a new set of issues was just going to
slow things down.

The final reason for sticking with WPF is that I'm not convinced that a
platform-neutral implementation is the right choice.  Some features, like
managing physical media, are specific to each platform.  Drag & drop
operations are provided by some GUI toolkits, but only within an application.
Copying files between program instances, or to and from a system
Finder/Explorer window, requires platform-specific handling.  The right answer
may be that every platform needs a custom implementation that can take full
advantage of the system's characteristics.

Since I don't know what the right answer is, I'm going to forge ahead with the
WPF version.  This will ensure that the various APIs work correctly with a GUI
app, and demonstrate how I think the tool should behave.  Fortunately, the GUI
application code is a relatively small part of the overall package.

## Source Code Division ##

As of v1.0, the division of code between components is approximately:

 - 4% generic libraries (CommonUtil)
 - 54% app-specific libraries (DiskArc, FileConv, AppCommon)
 - 14% library tests (DiskArcTests and FileConvTests)
 - 13% CLI (includes regression tests)
 - 13% WPF GUI
 - 1% miscellaneous (Examples, MakeDist)

(Based on Lines of Executable Code, from Visual Studio's "Calculate Code
Metrics" feature.)
