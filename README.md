# CiderPress II # 

### **This is pre-alpha software.  Use at your own risk.**

[Features](#features) - [Getting Started](#getting-started) - [Source/License](#source-code-and-license)

CiderPress II is a software tool for working with disk images and file archives associated
with vintage Apple computers, notably the Apple II line and early Macintoshes.  It runs on
modern desktop and embedded systems.

The program allows you to perform various operations on files stored in disk images and file
archives: list, add, extract, import, export, copy, rename, move, delete, test, print, and
set attributes.  You can create new disk images in various formats and sizes, get metadata,
set metadata, copy blocks or sectors, edit blocks or sectors, and view raw tracks.

For installation instructions, see the [install guide](Install.md).

**Current status:**
 - Video demo of v0.1 here: https://www.youtube.com/watch?v=_jDVdC6-eoA
 - The command-line tool is alpha quality.  The command set is feature-complete for v1.0, though
   support for some key formats is missing.  See [the manual](cp2/Manual-cp2.md) for a thorough
   description of commands and features.
 - The GUI tool is still under development, and should not be used by anyone;
   see notes in [GUI Tool Development](#gui-tool-development).

## Features ##

The goal is to have all features available from both the command line and the GUI.

File archive support:

Type                | Filename Extensions      | Status
------------------- | ------------------------ | ------
AppleLink Comp Util | .acu                     | not yet
AppleDouble         | ("._" prefix pair)       | add/extract
AppleSingle         | .as                      | read, add/extract
Binary ][           | .bny .bqy                | full support
gzip                | .gz                      | full support
NuFX (ShrinkIt)     | .shk .sdk .bxy .sea .bse | full support
Stuffit (vintage)   | .sit                     | not yet
ZIP                 | .zip                     | full support, including __MACOSX handling

"Add/extract" means that you can add or extract files to and from the format, e.g. adding an
AppleDouble file pair to an HFS disk image results in a single new file with a resource fork
and file type information.  AppleSingle files can be read like other file archives, but are
only created as part of extracting files from something else.

The __MACOSX support allows files in ZIP archives to be stored with resource forks and HFS
file types.

Compression modes supported are LZW/1 and LZW/2 for NuFX, Squeeze for Binary ][, and deflate for
ZIP/gzip.

Disk image support:

Type             | Filename Extensions              | Status
---------------- | -------------------------------- | ------
2IMG             | .2mg .2img                       | full support
Dalton Disk Dis. | .ddd                             | not yet
DiskCopy 4.2     | .dc .image                       | not yet
FDI              | .fdi                             | no
Trackstar        | .app                             | not yet
Unadorned Block  | .do .po .dsk .d13 .iso .hdv .img | full support
Unadorned Nibble | .nib .nb2                        | full support
WOZ              | .woz                             | full support (FLUX is read-only)

Disk images may be from Apple II 5.25" floppies, Apple 3.5" disks, hard drives, CD-ROMs, and other
block-addressable media.

Filesystem support:

Type             | Status
---------------- | ------
CP/M             | not yet
DOS 3.2/3.3      | full support (incl. 40/80 track disks)
Gutenberg        | not yet
HFS              | full support (up to 4GB volumes)
ProDOS           | full support
RDOS 3/3.2/3.3   | not yet
UCSD (Pascal)    | not yet

Multi-volume support:

Type                        | Status
--------------------------- | ------
AmDOS, OzDOS, UniDOS        | read/write
Apple Partition Map (APM)   | read/write
CFFA with 4/6/8 partitions  | read/write
DOS hybrids                 | read/write
DOS.MASTER embedded volumes | read/write
FocusDrive partitions       | not yet
MicroDrive partitions       | not yet
Early Mac 'TS' Format       | read/write

Creation of multi-volume disk images is not yet supported.

File conversion (export):

Type                         | Status
---------------------------- | ------
Plain text                   | convert EOL, character set
Random-access text           | record length configurable; output to CSV
Hex dump                     | character portion has selectable character set
Applesoft BASIC              | plain text or syntax-highlighted
AppleWorks Database          | not yet
AppleWorks Spreadsheet       | not yet
AppleWorks Word Processor    | not yet
Apple II Hi-Res              | 560x384 color or B&W, with half-pixel shifts
Apple IIgs Super Hi-Res      | 640x400 color; unpacked ($C1/0000)
Apple IIgs Super Hi-Res 3200 | 640x400 color; unpacked ($C1/0002)
Teach Document               | most formatting supported
*more to come*               | *goal is parity with original CiderPress*

Text conversions can treat the source as low/high ASCII, Mac OS Roman, or ISO Latin-1.
Hex dump skips over sparse sections of files.

 - Simple text is output as .TXT, using UTF-8 with host-specific end-of-line markers.
 - Formatted text is output as .RTF (Rich Text Format).
 - Bitmap graphics are output as .PNG (Portable Network Graphic).
 - Cell grid data is output as .CSV (Comma-Separated Value).

 File conversion (import):

Type                         | Status
---------------------------- | ------
Plain text                   | convert EOL, character set
Applesoft BASIC              | plain text to tokenized form

### Changes from CiderPress ###

The original CiderPress, first published in 2003, is a Windows-only application that can be
run on other platforms with the use of the WINE emulation wrapper.  The code was written in C++,
using the Windows MFC toolkit.  Some of the lower-level functions were implemented in portable
libraries that were shared with other applications.

CiderPress II is written in C#, targeted at .NET Core 6.  It gives equal importance to GUI and
command-line interfaces, and can run on a variety of Windows, Mac OS, and Linux systems.

Besides new features like a command-line interface and WOZ disk image support, there are a
few more subtle changes:

 - File archives and disk images nested inside other file archives and disk images can be accessed
   directly.
 - When files are extracted, the resource fork and extended attributes can be preserved in
   multiple ways: AppleSingle, AppleDouble, NAPS (NuLib Attribute Preservation Strings), or
   using host filesystem features (Mac OS / HFS+ only).  These are handled transparently when
   adding files.
 - DOS T/I/A/B files can be opened in "raw" mode.
 - Files may be copied directly between volumes.  For DOS files this can preserve the sparse
   structure of random-access text files.
 - AppleSingle and AppleDouble are integrated into add/extract.  In the original, AppleSingle was
   treated as a read-only archive.
 - DOS hybrid (e.g. DOS + ProDOS on a single disk) support has been added, and the handling of
   DOS.MASTER embedded volumes has been greatly improved.
 - HFS file type support has been generalized.  ProDOS and HFS types can be set independently in
   places where both are present (NuFX archives, ProDOS extended files).
 - Errors and warnings generated by lower-level code, such as filesystem implementations, is now
   visible to the user.

A few things have been removed:

 - NuFX archives with deflate, bzip2, and LZC compression are no longer supported.
 - The FDI disk image format has been dropped.
 - SST file combining has been dropped.

Under the hood there are many significant changes, such as:

 - NufxLib and libhfs have been replaced.
 - The CiderPress disk image library had some file update limitations, notably that files had to
   be written all at once.  The new library returns a Stream object that can be used the same way
   it would for a file on the host filesystem.
 - Compression code uses the same API as the standard System.IO.Compression classes, making it
   easy to integrate NuFX LZW or Squeeze compression into code that doesn't want the rest of the
   NuFX archive handling.
 - The file conversion library returns platform-agnostic objects that can be converted to
   TXT/RTF/PNG/CSV, rather than returning Windows-specific bitmaps and pre-formatted RTF.

### GUI Tool Development ###

During development, the command-line interface was developed first.  The graphical interface is
currently being prototyped with WPF (Windows Presentation Foundation).  This might seem an odd
choice, since WPF is several years old and Windows-only, and there are multi-platform UI toolkits.
The motivations are:

 1. Some multi-platform UI toolkits don't work on all of my target platforms.  For example,
    Microsoft's MAUI doesn't currently work on Linux.
 2. Some UI toolkits emphasize mobile device interaction, making them less suitable for what is
    intended as a desktop application.
 3. Some UI toolkits are missing important pieces, like robust display of formatted text.
 4. I'm familiar with WPF.

My goal with the WPF implementation was to provide a useful program that proves out the API in the
underlying libraries.  The bulk of the implementation is shared between the CLI and GUI apps,
but you can't trust an API until it has been used for a real application.  I had initially planned
to use a platform-independent UI toolkit for the first version, but I wasn't able to find one
that seemed both appropriate and ready.  WPF has a lot of issues, but I've already fought those
battles and know how to solve the problems.  Learning a new API and working through a new set of
issues was just going to slow things down.

The final reason for sticking with WPF is that I'm not convinced that a platform-neutral
implementation is the right choice.  Some features, like managing physical media, are specific
to each platform.  Drag & drop operations are provided by some GUI toolkits, but only within an
application.  Copying files between program instances, or to and from a system Finder/Explorer
window, requires platform-specific handling.  The right answer may be that every platform needs
a custom implementation that can take full advantage of the system's characteristics.

Since I don't know what the right answer is, I'm going to forge ahead with the WPF version.  This
will ensure that the various APIs work correctly with a GUI app, and demonstrate how I think the
tool should behave.  Fortunately, the GUI application code is a relatively small part of the
overall package.

## Getting Started ##

[ TODO ]

See [the manual](cp2/Manual-cp2.md) for instructions on using the command-line "cp2" tool.
You will need a command shell for your system, such as Terminal for the Mac, or PowerShell
for Windows.

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
