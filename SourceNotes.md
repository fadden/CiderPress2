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
 - Examples/*: some simple command-line applications that demonstrate
   the use of the libraries.

Other (not included in binary distribution):

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
