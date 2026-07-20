# CiderPress II Build & Packaging Tools #

There are several steps involved in publishing a new release.  Start by
updating version numbers and running a final set of tests on the app.

 1. Update the version numbers in `AppCommon/GlobalAppVersion.cs`.  This
    changes the version number displayed by the applications, and the
    version string used by build scripts.
 2. Update the version number in `DiskArc/Defs.cs`.  This just tracks
    GlobalAppVersion.  It's stored redundantly because the library is
    intended to be separable from the application.  (You don't need to
    update this for a non-final release.)
 3. Do a full build of the project in Visual Studio, in Debug mode.
 4. Launch the GUI application (`cp2_avalonia` project, built as
    `CiderPress2.exe`) in the debugger.  Verify the version number.  It's
    important to do this because, in a Debug build, some library unit tests
    are executed during app startup.
 5. Run the command-line tests: `cp2 debug-test`, `cp2 debug-test-da`,
    and `cp2 debug-test-fc`.  These require files in the TestData directory.
    All tests must pass.  If you're not built for Debug mode, you will see
    a warning at the end of the test runs.  Ideally these would be run on
    all supported platforms (Win10+, macOS 11+, Linux).

If this is a "final" release, you will need to publish updated documentation,
from the "ndocs" directory to the live github website "docs" directory.  You
should not do this for pre-releases, because the web site contents should
always match the current "final" release.

 6. If file format docs have changed, use the `ndocs/formatdoc/convert.py`
    script (see below).
 7. From the command line, change into the `ndocs` directory and run
    `publish.py` (you may need to explicitly run python if the shell isn't
    set up to execute scripts directly).  This will update the contents of
    the `docs` directory and the top-level documentation (including this file)
    with the contents of `ndocs`.  Check the diffs.

Finally, build the applications and submit the changes.

 8. Run `Pkg/make-dist.py` from the top of the source tree.  This builds the
    projects in Release mode and assembles the distribution packages.  The
    output will be in the `DIST` directory.
 9. Submit all changes to git, push them to the server.
 10. Create a new release on github.  Drag `DIST/*.zip` into the release.
 11. Update/close any issues that have been addressed by the new release.

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
