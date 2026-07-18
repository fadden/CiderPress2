# CiderPress II Source Code Notes #

All of the code is written in C# .NET, using the (free to download) Visual
Studio Community 2026 IDE as the primary development environment.  All of
the code targets .NET 10, and none of the code has a machine-specific target.
The projects can be built for a 32-bit or 64-bit environment.

When installing Visual Studio, be sure to include ".NET Desktop Development".

The [MakeDist](MakeDist/README.md) command handles building and packaging for
releases.  You will need a full .NET SDK installation to do builds (with
`dotnet build` if you want to use the command line).

The source files that implement disk and file formats have accompanying
"-notes" documents that describe the formats in some detail, and have
references to primary sources.

## Projects ##

The "solution" is comprised of several projects.

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

 - cp2: command-line application (regression tests are built in).
 - cp2_avalonia: GUI application, built with Avalonia toolkit.
 - cp2_wpf: GUI application for Windows only (deprecated).

Other (not included in binary distribution):

 - Examples/AddFile and Examples/ListContents: simple command-line
   applications that demonstrate the use of the libraries.
 - MakeDist: build utility that creates distribution packages.
 - TestData: not actually a project.  This is a collection of file archives
   and disk images, used by the DiskArc library tests and the cp2
   command-line application tests.

## Tests ##

The tests in the DiskArcTests and FileConvTests libraries can be run in two
different ways:

 1. `cp2 debug-test-da` and `cp2 debug-test-fc`
 2. From the hidden "DEBUG" menu in the cp2_wpf application.

The cp2 command-line application has its own set of tests that can be run with
`cp2 debug-test`.  See the [cp2 test README](cp2/Tests/README.md).

All tests make use of the test files stored in the TestData directory.

The hidden `--debug` option for cp2 will show additional debugging information
for certain operations.  For example, `cp2 help --debug` also lists the
commands used to run the tests, and `cp2 version --debug` will display
additional information about the runtime environment.

In addition, the GUI application has a "bulk compression test" in the DEBUG
menu that will compress all files in a disk image or file archive with a
specific compression algorithm, then decompress them and verify that the output
matches the input.  This can be used for performance and correctness testing
of compression code.

## Source Code Proportions ##

As of v1.0, the division of code between components is approximately:

 - 4% generic libraries (CommonUtil)
 - 54% app-specific libraries (DiskArc, FileConv, AppCommon)
 - 14% library tests (DiskArcTests and FileConvTests)
 - 13% CLI (about half of which is regression tests)
 - 13% WPF GUI
 - 1% miscellaneous (Examples, MakeDist)

(Based on Lines of Executable Code, from Visual Studio's "Calculate Code
Metrics" feature.)
