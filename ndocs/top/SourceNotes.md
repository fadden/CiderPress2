# CiderPress II Source Code Notes #

All of the code is written in C# .NET, using the (free to download) Visual
Studio Community 2022 IDE as the primary development environment.  All of
the code targets .NET 8.  With the exception of the WPF application,
none of the code has a machine-specific target.  The projects can be built
for a 32-bit or 64-bit environment.

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

 - cp2: command-line application for Windows, Linux, Mac, et.al. (regression
   tests are built in).
 - cp2_wpf: GUI application for Windows.

Other (not included in binary distribution):

 - Examples/*: some simple command-line applications that demonstrate
   the use of the libraries.
 - MakeDist: build utility that creates distribution packages.
 - TestData: not actually a project.  This is a collection of file archives
   and disk images, used by the DiskArc library tests and the cp2
   command-line application tests.

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

In addition, the GUI application has a "bulk compression test" in the DEBUG
menu that will compress all files in a disk image or file archive with a
specific compression algorithm, then decompress them and verify that the output
matches the input.  This can be used for performance and correctness testing
of compression code.

## GUI Tool Development ##

During development, the command-line interface was developed first.  The
graphical interface is currently implemented with WPF (Windows Presentation
Foundation).  This might seem an odd choice, since WPF is several years old
and Windows-only, and there are multi-platform UI toolkits.  The motivations
for using it are:

 1. Some multi-platform UI toolkits don't work on all of my target platforms.
    For example, Microsoft's MAUI doesn't currently work on Linux.
 2. Some UI toolkits emphasize mobile device interaction, making them less
    suitable for what is primarily a desktop application.
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

## Publishing a New Release ##

There are several steps involved in publishing a new release.  Start by
updating version numbers and running a final set of tests on the app.

 1. Update the version number in `AppCommon/GlobalAppVersion.cs`.  This
    changes the version number displayed by the applications, and
    determines the version in the filenames generated by MakeDist.
 2. Update the version number in `DiskArc/Defs.cs`.  This just tracks
    GlobalAppVersion.  It's stored redundantly because the library is
    intended to be separable from the application.  (You don't need to
    update this for a non-final release.)
 3. Do a full build of the project in Visual Studio, in Debug mode.  Besides
    confirming that everything works, it builds MakeDist with the updated
    version number.  It's important to build for Debug to enable assertions
    and extended checks.
 4. Launch the GUI application (`cp2_wpf` project, built as `CiderPress2.exe`)
    in the debugger.  Verify the version number.  It's good to do this
    because, in a Debug build, some library unit tests are executed during
    app startup.
 5. Run the command-line tests: `cp2 debug-test`, `cp2 debug-test-da`,
    and `cp2 debug-test-fc`.  These require files in the TestData directory.
    All tests must pass.  If you're not built for Debug mode, you will see
    a warning at the end of the test runs.  Ideally these would be run on
    all supported platforms (Win10+, macOS 11+, Linux).

If this is a "final" release, you will need to publish updated documentation,
from the "ndocs" directory to the live github website "docs" directory.  You
should not do this for pre-releases, because the web site contents should
always match the current "final" release.

 6. Update the `app_version` number in `ndocs/publish.py`.  This is used
    for text substitution in the descriptive text and installer links.
 7. If file format docs have changed, use the `ndocs/formatdoc/convert.py`
    script (see below).
 8. From the command line, in the `ndocs` directory, run `publish.py`
    (you may need to explicitly run python if the shell isn't set up to
    execute scripts directly).  This will update the contents of the `docs`
    directory and the top-level documentation (including this file) with the
    contents of `ndocs`.  Check the diffs.

Finally, build the applications and submit the changes.

 9. Run `makedist build` from the top level of the source tree (it'll be in
    `MakeDist/bin/debug/NET6.0`).  This builds the distribution packages
    in Release mode.  The output will be in the `DIST` directory.
 10. Submit all changes to git, push them to the server.
 11. Create the pre-packaged Wine release for Mac OS.  (This requires
     performing several steps on a Macintosh.  See the
     [Wine Notes](WineNotes.md) document for more information.)
 12. Create a new release on github.  Drag `DIST/*.zip` into the release.
 13. Update/close any issues that have been addressed by the new release.

Version numbers should follow the semantic versioning scheme: v1.2.3,
v1.2.3-dev1, etc.

There is an additional step for publishing updates to the files in the file
format documentation set (all of the "*-notes.md" files).  The script
`ndocs/formatdoc/convert.py` will generate HTML versions of the Markdown
documentation.  This is necessary because the major search engines don't like
to index github repositories.  The conversion script requires an external
program, and may need github authentication to avoid being cut off by the
rate limiter (the HTML conversion is actually performed by the github web
API), so it's not part of the publication script. The `ndocs/publish.py`
script just copies them to the `docs` directory.  Check the comments at the
top of `convert.py` for further instructions.

Modified "-notes.md" files can usually be converted at the time they are
updated, since their contents aren't really tied to a release.  Doing a full
refresh as part of generating a "final" release is still prudent to avoid
missing any changes.
