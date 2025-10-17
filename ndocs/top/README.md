# CiderPress II #

CiderPress II is a software tool for working with disk images and file archives associated
with vintage Apple computers, notably the Apple II line and early Macintoshes.  It runs on
modern desktop and embedded systems.

The program allows you to perform various operations on files stored in disk images and file
archives: list, add, extract, import, export, copy, rename, move, delete, test, print, and
set attributes.  You can create new disk images in various formats and sizes, get metadata,
set metadata, copy blocks or sectors, edit blocks or sectors, and view raw tracks, as well
as copy and replace whole disk partitions.

For installation instructions, see the [install guide](Install.md).

**Current status:**
 - The command-line tool runs on Windows, macOS, and Linux (anywhere .NET 8 runs).
 - The desktop GUI is written for Windows, but [can be made to run](WineNotes.md) on other
   systems with Wine emulation.
 - Brief (3 min) video introduction to v1.0: https://youtu.be/ZrUfNzscq3g

## Getting Started ##

Tutorials and reference documentation are available on the
[web site](https://ciderpress2.com/).

A variety of disk images and file archives used for testing can be found in the
[TestData](TestData) directory.

## Source Code and License ##

See [Source Notes](SourceNotes.md) for a brief tour of the source tree.

The source code is licensed under Apache 2.0
(http://www.apache.org/licenses/LICENSE-2.0), which makes it free for use in
both open-source programs and closed-source commercial software.  The license
terms are similar to BSD or MIT, but with some additional constraints on
patent licensing.  (This is the same license Google uses for the Android
Open Source Project.)

Images and documentation are licensed under Creative Commons ShareAlike 4.0 International
(https://creativecommons.org/licenses/by-sa/4.0/).
