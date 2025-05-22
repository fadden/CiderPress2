# AppleSingle / AppleDouble Wrapper #

## Primary References ##

- v1: Apple II File Type Note $e0/0001 (AppleSingle File)
- v1: Apple II File Type Note $e0/0002 $e0/0003 (AppleDouble Header/Data)
- v2: _AppleSingle/AppleDouble Formats for Foreign Files Developer's Note_
      (https://nulib.com/library/AppleSingle_AppleDouble.pdf)
- v2: RFC 1740 (https://www.rfc-editor.org/rfc/rfc1740.txt)

## General ##

AppleSingle is Apple's preferred way to store a forked file as a single file on a "foreign"
filesystem, along with all of its attributes.  The format does not support compression or
provide a checksum; it is simply a way to wrap files.  It can be used as a way to preserve
file attributes for file transfers or archiving, or at a lower level to store forked files on
an otherwise incompatible filesystem without the user being aware of the details.

There are two versions of the specification.  Version 1 is described by the A/UX documentation
and in an Apple II file type note.  Version 2 is described in a stand-alone document.  The
chief difference is a change to the preferred ways of storing file attributes.

On the Apple II, GS/ShrinkIt generates and opens v1 files.  It does not recognize v2 files.

There is no standard filename extension for AppleSingle, though ".as" is common.

### AppleDouble ###

AppleDouble stores the data and resource forks in two separate files.  The data file is simply
the data fork.  The "header" file is just an AppleSingle archive without a data fork, so it holds
the resource fork and/or file attributes.  Version 1 of the AppleDouble specification defined a
"data pathname" entry ID (100), but v2 just mentions it in passing.

Various filename tweaks are recommended for the AppleDouble header file: ProDOS files should be
prefixed with "R.", MS-DOS should end in ".ADF" (AppleDouble File), UNIX should prefix with "%".
Some applications put the files in a parallel ".AppleDouble" folder.  On modern systems, the
trend is to store the header file next to the data file, prefixing it with "._" and treating it
as hidden.

Modern Mac OS ZIP archive tools create AppleDouble header files when adding files with resource
forks or extended attributes, but prefix them with `__MACOSX/`, so that they appear as a parallel
tree when extracted.  These are v2 files with a non-empty file system string of `Mac OS X`.  They
appear to have entry IDs 9 (Finder Info, extended to hold a list of file attributes) and
2 (resource fork, may be zero-length).  These entries are created for directories as well as files.

On the Apple II, AppleDouble header files should use type $e0/0002, but the data file may use
the file's natural type.  For example, if it's a plain text file, it can be convenient to give
the data file type TXT/0000.  Using $e0/0003 for the data file is optional.

## Structure ##

All multi-byte values are stored in big-endian order.  Integers are unsigned unless otherwise
noted.

The file has a brief header, followed by zero or more 12-byte entry headers.  The entries hold
the file offset and length of the associated data.

Header:
```
+$00 / 4: signature (0x00 0x05 0x16 0x00)
+$04 / 4: version (0x00 0x01 0x00 0x00 (v1) -or- 0x00 0x02 0x00 0x00 (v2))
+$08 /16: home file system string (v1) -or- zeroes (v2)
+$18 / 2: number of entries
```
Entries:
```
+$00 / 4: entry ID (1-15)
+$04 / 4: offset to data from start of file
+$08 / 4: length of entry in bytes; may be zero
```
For AppleDouble, the signature changes to 0x00 0x05 0x16 0x07.

For zero-length entries, the offset may be equal to the file's length.

### Entry IDs ###

Values for entry ID:

1. Data Fork: just holds the file data.  May be zero bytes long.

2. Resource Fork: just holds the file data.  May be zero bytes long.

3. Real Name: docs say "file's name as created on home file system".  In v1 that could be
matched up with the "home file system" value in the header, but in v2 this provides no insight
into the character encoding.  The Mac OS X tool creates UTF-8-encoded filenames.  The filename's
length is determined by the entry header, so null characters are allowed.  This is a simple
filename, not a partial path.  The v2 spec discusses a means of escaping high-ASCII characters
with '%', but apparently that's only intended for UNIX systems, despite there being no indication
of filesystem type in the v2 archive.  (Rule: use Mac OS Roman for v1 ProDOS/HFS, and UTF-8 for
everything else.)

4. Comment: a "standard Macintosh comment".  This appears to be Finder-related.

5. Icon, B&W: a "standard Macintosh black and white icon".  Examples of this and entry ID 6 are
rare.

6. Icon, Color: a "Macintosh color icon".

7. File Info (version 1 only): different layouts depending on the Home File System value.
The v1 spec describes entries for ProDOS, Mac, MS-DOS, and UNIX; most of it is various date/time
values, in system-specific encodings.

8. File Dates Info: creation, modification, backup, and access timestamps, expressed as signed
32-bit values, with Jan 1 2000 GMT as the zero point.  Unknown values should be set to $80000000.

9. Finder Info: the usual pair of 16-byte Finder data structures (FInfo/FXInfo).  HFS creator and
file type are stored here.  In Mac OS X AppleDouble files, the 32-byte Finder data is followed
by "ATTR" and a list of extended file attributes.

10. Macintosh File Info: the specification describes a 32-bit value, in which the low 2 bits
hold the "locked" and "protected" flags.  In practice the field is 8 bytes long.

11. ProDOS File Info: access, file type, aux type (GS/OS widths).

12. MS-DOS File Info: 16 bits of file attributes.

13. AFP Short Name: string.

14. AFP File Info: 4-byte value.

15. AFP Directory ID: 4-byte value.

Entries 8 and 10+ were added in version 2.

Version 1 used ID #7 to store file info, changing the layout based on the "home file system"
value.  Version 2 stores the dates in an entry with ID #8, and then uses a system-specific
entry to hold the remaining file attributes.

The v2 docs note, "entry IDs 1, 3, and 8 are typically created for all files; entry ID 2 for
Macintosh and ProDOS files, ...".  None of the entries are mandatory, however, and archives
without filenames are created by some tools.

Entry IDs from 1 to $7fffffff are reserved by Apple.  The rest of the range is available for
applications to define their own entries.  Apple does not arbitrate the usage.  Applications are
expected to ignore unknown entities, but preserve them when making changes or copies.

There is no restriction on the order of entries, though most tools output the filename first,
followed by attributes, then finally the file data.  This makes it easier to append additional
data to the file.  For AppleSingle the data fork should come last, since it's the most likely to
be appended to, and for an AppleDouble file the resource fork should be the last entity in the
header file.

It's valid to leave "holes" by placing the offset of an entry past the end of a previous entry.
For example, even if the comment field is only 10 bytes long, you could put the next entry 200
bytes past the start to leave room for the comment to grow.  Apple recommends allocating the
resource fork in 4K chunks in AppleSingle files to minimize file rewriting during updates.  This
is not necessary when the format is used for file transfer or archival purposes.

## Miscellaneous ##

Mac OS X provides an "applesingle" command-line tool for creating and unpacking AppleSingle files.
Unfortunately, after the OS was updated to work with X86 CPUs, early releases of the tool failed
to correct for the reversed byte ordering, and the tool started creating files with little-endian
values.  The broken tool did work correctly with AppleSingle files created by other programs,
but crashed when attempting to process its own output.  This was eventually corrected.

Normally it's best to generate files with the latest version of the specification, but for
compatibility with Apple II utilities like GS/ShrinkIt, generating v1 files may be wiser.  OTOH,
AppleSingle files are not normally consumed on an Apple II.
