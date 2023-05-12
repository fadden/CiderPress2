# Binary II Archive #

## Primary References ##

- Apple II File Type Note $e0/8000
- Initial Binary II specification (https://wiki.preterhuman.net/Apple_II_Binary_File_Format)

## General ##

Binary II was developed by Gary B. Little as a simple wrapper for ProDOS files that were sent
via modem.  Before it came along, there was no common way to preserve the file type, aux type,
dates, and other file attributes.  The format is intended for attribute preservation, not general
file storage, but for a while it was the standard way to archive files.

The format was designed to be easy to add and remove by telecommunications programs during the
file transfer.  Each file in the archive is preceded by a 128-byte header, and the end of the
file is padded to the nearest 128-byte boundary.  If there are multiple files, they are simply
concatenated together.  The only "global" information is stored in the "disk space needed"
and "number of files to follow" fields in the header of the first entry, although the disk space
needed value is optional.

The initial specification had some bolted-on support for ProDOS-16, and a later revision
updated it for GS/OS.  There is no provision for extended files; rather, you are directed to
wrap the file in AppleSingle format before wrapping it with Binary II.  GS/OS option lists are
supported through the "phantom file" mechanism, but it's unclear how widely this was used.

When files in subdirectories are included, they must be preceded by a zero-length entry for the
subdirectory itself.  While this is usually a waste of space -- the receiving program can easily
create missing subdirectories when a file creation call fails -- it does allow the transmittal
of empty directories.

While the format definition made an effort to support non-ProDOS files, in practice it doesn't
appear to have been used for that.  By the time GS/OS FSTs were a thing, ShrinkIt had become
the standard way to package and distribute Apple II files.

## Version Differences ##

The initial "version 0" specification was published Nov 24 1986.  Version 1 was documented
in an Apple II File Type Note, dated July 1989.

The following changes were made in version 1:
 - The filename field was redefined to be a filename or partial pathname.  If it holds a
   filename of 15 or fewer characters, it may be followed by a string with the "ASCII value
   of the native filename".  This appears to be a way to capture the original DOS 3.3 filename
   alongside the ProDOS equivalent.
 - Added two bytes for the high word of the GS/OS auxiliary type.
 - The "operating system type" byte was redefined to *almost* match the GS/OS definitions.
   The original spec used 0=ProDOS, 1=DOS 3.3, 2=Pascal, 3=CP/M, 4=MS-DOS.  The revised spec
   uses 0=ProDOS, 1=DOS 3.3, 2=reserved, and then matches GS/OS after that.
 - Additional phantom file ID codes were defined.
 - The version number field was increased to 1.

The updated specification also mentions use of "squeeze" compression, recommending the use of
".BQY" as the filename extension when compressed files are present.  Squeezed files include
the full file header, with the magic number and checksum.

## Structure ##

The version 1 header is a superset of the version 0 header, except for the redefined operating
system type field.

```
+$00 / 3: ID bytes ($0a $47 $4c) - ASCII linefeed, 'G', 'L'
+$03 / 1: ProDOS access byte
+$04 / 1: ProDOS file type
+$05 / 2: ProDOS aux type
+$07 / 1: ProDOS storage type
+$08 / 2: size of file, in 512-byte blocks; includes ProDOS overhead (index blocks)
+$0a / 2: ProDOS modification date
+$0c / 2: ProDOS modification time
+$0e / 2: creation date
+$10 / 2: creation time
+$12 / 1: ID byte ($02)
+$13 / 1: (reserved)
+$14 / 3: length of file, in bytes
+$17 /65: file name or partial pathname, preceded by length byte; max 64 chars
 +$27 /49: optional native name (embedded in above field)
+$58 /15: (reserved)
+$6d / 2: GS/OS aux type (high word)
+$6f / 1: GS/OS access (high byte)
+$70 / 1: GS/OS file type (high byte)
+$71 / 1: GS/OS storage type (high byte)
+$72 / 2: GS/OS file size in blocks (high word)
+$74 / 1: GS/OS EOF (high byte)
+$75 / 4: disk space needed for ALL files in this archive (optional; set in first entry only)
+$79 / 1: operating system type
+$7a / 2: native file type (16 bits of OS-specific type data; suggested for DOS 3.3 files)
+$7c / 1: phantom file flag
+$7d / 1: data flags; indicates compressed/encrypted/sparse
+$7e / 1: version ($00 or $01)
+$7f / 1: number of files to follow (including phantoms)
```

Filenames must be in ProDOS 8 format.  If the filename field holds a simple filename, not a
partial pathname, the "native name" field can hold the original filename (e.g DOS or Pascal).

The original specification notes, "Some file attributes returned by ProDOS 16 commands are
one or two bytes longer than the attributes returned by the corresponding ProDOS 8 commands.
At present, these extra bytes are always zero, and probably will remain zero forever".  This
prediction was accurate.

## BLU ##

The Binary II Library Utility, or BLU, was written by Floyd Zink, Jr. to provide a way to
pack and unpack Binary II files.  For a time, this was the standard tool for packaging a set of
files for distribution.  As such, its behavior defines how Binary II files must be handled.

When adding files to an archive, BLU does not attempt to determine if they are already
squeezed, e.g. by SQ3.  The "compressed" bit in the data flags will be set if BLU does the
squeezing itself, but in general the flag cannot be relied upon.  When extracting files, BLU
checks the "compressed" data flag first.  If it's not set, it then checks to see if the
filename ends in ".QQ".  If so, the file is treated as compressed; otherwise not.

Directories are stored with their length and file size copied from the directory entry, rather
than set to zero as required by the specification.

The program has a stealth "help" screen: use Open-Apple '/' to see the instructions.  The
ability to squeeze files is otherwise unfindable in v2.28, which removed stand-alone file
compression from the main menu.

The source code for v2.28 was included in the first issue of "8/16 on Disk", published March 1990.

Trivia: BLU sets the last byte in the filename buffer (+$57) to $5a, which is ASCII 'Z'.  Probably
a reference to the author.

## Miscellaneous ##

The field that holds the file size in blocks will generally be copied directly from the ProDOS
file info call, so it includes the ProDOS filesystem overhead.  The actual storage required to
extract the file may be more or less than what is indicated, especially if the file has sparse
regions.  When creating an archive, it's probably best to generate the field as a "worst case"
ProDOS 8 value, or just leave it set to zero.

ProDOS directories may be identified by their storage type ($0d) or file type (DIR/$0f).  The
Binary II specification seems to recommend using the file type.  An entry that mixes these up
should be ignored, although it can be awkward to do so since the interpretation of the file
length field may require knowing if the entry is a directory.
