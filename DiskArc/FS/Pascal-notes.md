# Apple Pascal Filesystem for the Apple II #

## Primary References ##

 - _Apple II Pascal 1.3_, https://archive.org/details/apple-ii-pascal-1.3/mode/2up
   (directory format on page IV-15)
 - _Apple Pascal Operating System Reference Manual_,
   https://archive.org/details/Apple_Pascal_Operating_System_Reference_Manual/mode/1up
   (disk format starts p.25, file format descriptions p.265)

## General ##

In 1977, the University of California, San Diego (UCSD) Institute for Information Systems
developed the UCSD Pascal system to provide a common environment for campus computing.
Version II of the UCSD p-System was ported to the Apple II by Apple Computer, and released in
August 1979.

The Apple II version came with a new operating system that used 16-sector 5.25" disks divided
into 512-byte blocks, different from Apple's current DOS 3.2 OS, which used 13-sector disks
with 256-byte sectors.  (DOS 3.3 didn't ship until the following year.)  The disk filesystem
format is generally referred to as "Pascal", even though it had no ties to the programming
language.

All files are listed in a single directory structure.  The directory spans multiple blocks, but
is not block-oriented: file entries can span block boundaries.  The expectation is that the
directory is read in its entirety, modified, and written back.  Rewriting the full directory is
required for certain operations, e.g. when a file is deleted, all following entries are shifted
up one slot.

The disk doesn't have a volume allocation bitmap.  Instead, each file has a start block and a
length, and the contents are stored on contiguous blocks.  This makes accesses very fast, but
creates problems with fragmentation.  It's also likely that attempting to append data to the
end of a file will fail.

The system doesn't define an explicit maximum file length.  Untyped files are accessed as whole
blocks, by block number, which is a signed 16-bit integer.  This yields a maximum size of 16MB.

Pascal volumes can be stored on 140KB 5.25" disks, 800KB 3.5" disks, and in special regions of
ProDOS volumes.  The exact specifications of the latter are unknown, but it was managed by
Apple's "Pascal ProFile Manager", for use with the Apple ProFile hard drive.  According to the
manual, the region was fixed in size and occupied a contiguous region of the ProDOS volume.

Volume names are limited to 7 ASCII characters, and may not contain equals ('='), dollar ('$'),
question ('?'), or comma (',').  Filenames are limited to 15 characters, and in theory all
characters are legal.  However, the filesystem is case-insensitive, and removes spaces and
non-printing characters.  In addition, it can be difficult to use the file utilities if the
filename includes dollar ('$'), left square bracket ('['), equals ('='), a question mark ('?'),
or various control characters.  Colon (':') is used to indicate a device/volume name, and should
be avoided as well.  (Summary: use printable ASCII not in `$=?, [#:`.)

The Pascal system converts volume and file names to upper case, but may not do case-insensitive
comparisons.  For example, a volume called "Foo" will not be accessible as "foo:" from the Filer,
but a volume called "FOO" will.  Volume and file names should always be written to disk in
upper case.

### File Types ###

The directory entry includes a file type value.  Some parts of the system also expect a filename
extension to be present.  The manual notes that changing the filename with the file utilities may
result in a mismatch, so the two aren't tied together.  (You can see the file types with the
"extended listing" command in the Filer.)  The defined types are:
```
 0 untypedfile - used for the volume directory header entry
 1 xdskfile / .BAD - used to mark physically damaged disk blocks
 2 codefile / .CODE - machine-executable code
 3 textfile / .TEXT - human-readable text
 4 infofile / .INFO - (not used)
 5 datafile / .DATA - arbitrary data
 6 graffile / .GRAF - (not used)
 7 fotofile / .FOTO - (not used)
 8 securedir - (unknown)
```
Note that .TEXT files have a specific structure based around 1024-byte pages, and encode runs
of leading spaces in compressed form (very handy for Pascal source code).  See page IV-16 in the
Pascal 1.3 manual for a description.  .CODE files are described on page IV-17.  "Untyped"
file access is discussed in the file I/O chapter (e.g. page III-180), though the relationship
between the "untyped" file type and untyped file access isn't clear to me.

## Disk Structure ##

Blocks 0 and 1 are reserved as boot blocks.  The directory starts in block 2.  All disks have a
2048-byte directory spanning blocks 2 through 5 (inclusive), regardless of volume size.  Each
directory entry is 26 bytes long, providing space for 78 entries.  The first entry holds a volume
header, so a disk can hold up to 77 files.  (The Apple Pascal 1.3 manual warns that, while the
ProFile hard drive can be formatted as a Pascal volume, it will still be limited to 77 files.)

The volume directory entry is:
```
+$00 / 2: system area start block number (always 0)
+$02 / 2: next block (first block after directory; always 6)
+$04 / 2: file type ($00)
+$06 / 8: volume name, prefixed with length byte
+$0e / 2: number of blocks in volume
+$10 / 2: number of files in directory
+$12 / 2: last access time (declared as "integer", definition unclear; always zero?)
+$14 / 2: most recently set date value
+$16 / 4: (reserved)
```
The "most recently set date" is used to store the last date that was set for the system.  This is
useful on systems without a clock card, where the date is entered manually by the user.

A regular directory entry is:
```
+$00 / 2: file start block number
+$02 / 2: first block past end of file (i.e. last block + 1)
+$04 / 2: file type in bits 0-3; bit 15 used "for filer wildcards"; other bits reserved
+$06 /16: file name, prefixed with length byte
+$16 / 2: number of bytes used in last block
+$18 / 2: modification date
```
All multi-byte integers are stored in little-endian order.

Directory entries are packed together.  Entries are sorted by starting block number, so when a
file is created it may be necessary to slide the following entries down.  When an entry is deleted,
the entries that follow are moved up.  Unused regions of the disk do not have directory entries;
their existence is implied when the "next block" of one file is not equal to the "start block"
of the next file.

Entries past the end of the list have their filename lengths set to zero.  It is possible to
"undelete" a file by making a new one with the Filer, in the same space with the same size.

To see file types and empty disk regions, request an E)xtended directory listing from the F)iler.

Entries with zero in the "bytes used" field will be silently removed when the system boots,
regardless of how many blocks they occupy.  As of Pascal 1.1, entries with zero blocks
(start == next) but a nonzero byte count are not removed (note these are illegal).

Because files are stored in contiguous blocks, fragmentation can become a problem very quickly.
The Apple Pascal Filer tool provides a "K(runch" command that defragments the disk.  The process
needs to work around any "bad block" files encountered, since those represent physically damaged
disk sectors and cannot be moved.

### Timestamps ###

The filesystem stores the date, but not the time.  Dates are held in a 16-bit value:
```
 YYYYYYY DDDDD MMMM
```
Years are 0-99, starting in 1900.  Months are 1-12, days are 1-31.  A zero value in the month
field indicates "meaningless date".

The Filer utility uses a date with the year 100 as a flag to indicate file creation in progress.
If the system finds a file with year >= 100, it will silently remove the file, assuming that it
was left over from a failed file creation attempt.

The recommended way to handle dates for the year 2000 and beyond is to adopt Apple's preferred
approach for ProDOS: encode 1940-1999 as 40-99, and 2000-2039 as 0-39.

### Apple System Utilities ###

Apple's System Utilities 3.1 for ProDOS appears to handle Pascal disks incorrectly.  When files
are deleted, the directory entry is zeroed out but not removed, and the file count is decremented.
When next booted, the Pascal operating system removes the bogus entry and decrements the file
count, resulting in an incorrect count.
See https://groups.google.com/g/comp.sys.apple2.programmer/c/m6Ym3bMGlQg/m/BE4mHmGkKecJ
