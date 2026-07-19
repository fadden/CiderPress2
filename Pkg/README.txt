CiderPress II

Web site: https://ciderpress2.com/
Source: https://github.com/fadden/ciderpress2

CiderPress II is a software tool for working with disk images and file
archives associated with vintage Apple computers, notably the Apple II line
and early Macintoshes. It runs on modern desktop and embedded systems.

The program allows you to perform various operations on files stored in disk
images and file archives: list, add, extract, import, export, copy, rename,
move, delete, test, print, and set attributes. You can create new disk images
in various formats and sizes, get metadata, set metadata, copy blocks or
sectors, edit blocks or sectors, and view raw tracks, as well as copy and
replace whole disk partitions.

This is free software, distributed under the terms of the Apache 2.0 license.


--- Installation on Windows and Linux ---

On Windows and Linux, there is no installation step.  It should be possible
to unzip the distribution archive and run the programs immediately.

--- Installation on macOS ---

On macOS, you will need to clear the "quarantine" flag first.  This flag is
set on all files downloaded from the internet.  From a terminal window,
change to the directory where the files were unpacked, and then run:

  xattr -dr com.apple.quarantine *

The GUI application is distributed as a ".app", which can be launched from
the Finder by double-clicking on it.  The command-line tool is currently
placed inside the .app directory hierarchy, and can be accessed as
`./CiderPress\ II.app/Contents/MacOS/cp2`.  Creating a symlink elsewhere
in your path is recommended.

--- Troubleshooting ---

If you get an error message indicating that the .NET framework could not be
found, try using a self-contained build instead.
