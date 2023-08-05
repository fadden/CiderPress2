# Apple Pascal Filesystem for the Apple II #

## Primary References ##

 - _Apple Pascal Operating System Reference Manual_,
   https://archive.org/details/Apple_Pascal_Operating_System_Reference_Manual/mode/1up
   (disk format starts p.25, file format descriptions p.265)
 - _Apple II Pascal 1.3_, https://archive.org/details/apple-ii-pascal-1.3/mode/2up
   (directory format on page IV-15)

## General ##

In 1977, the University of California, San Diego (UCSD) Institute for Information Systems
developed the UCSD Pascal system to provide a common environment for campus computing.
Version II of the UCSD p-System was ported to the Apple II by Apple Computer, and released in
August 1979.

The Apple II version came with a new operating system that used 16-sector 5.25" disks divided
into 512-byte blocks, different from Apple's other operating system that used 13-sector disks
with 256-byte sectors.  (DOS 3.3 didn't ship until the following year.)  The disk filesystem
format is generally referred to as "Pascal", even though it had no real ties to the programming
language.

All files are listed in a single directory structure.  The directory spans multiple blocks, but
is not block-oriented: file entries can span block boundaries.  The expectation is that the
directory is read in its entirety, modified, and written back.  Rewriting the full directory is
required for certain operations, e.g. when a file is deleted, all following entries are shifted
up one slot.

The disk doesn't have a volume allocation bitmap.  Instead, each file has a start block and a
length, and the contents must be stored contiguously.  This makes accesses very fast, but
creates problems with fragmentation.  It's also likely that attempting to append data to the
end of a file will fail.

Pascal volumes can be stored on 140KB 5.25" disks, 800KB 3.5" disks, and in special regions of
ProDOS volumes.  The exact specifications of the latter are unknown, but it was managed by
Apple's "Pascal ProFile Manager", for use with the Apple ProFile hard drive.  According to the
manual, the region was fixed in size and occupied a contiguous region in the ProDOS volume.

Volume names are limited to 7 ASCII characters, and may not contain equals ('='), dollar ('$'),
question ('?'), or comma (',').  Filenames are limited to 15 characters, and in theory all
characters are legal.  However, the filesystem is case-insensitive, and removes spaces and
non-printing characters.  In addition, it can be difficult to use the file utilities if the
filename includes dollar ('$'), left square bracket ('['), equals ('='), a question mark ('?'),
or various control characters.  Note ':' is used to indicate a device/volume name, and should
be avoided as well.  (Summary: use printable ASCII not in `$=?, [#:`.)

### File Types ###

The directory entry allows a file type to be assigned.  Some parts of the system also expect
a filename extension to be present.  The manual notes that changing the filename with the file
utilities may result in a mismatch.  The defined types are:
```
 0 untypedfile - used for "untyped" files and volume header entry
 1 xdskfile / .BAD - used to mark physically damaged disk blocks
 2 codefile / .CODE - machine-executable code
 3 textfile / .TEXT - human-readable text
 4 infofile / .INFO - (not used)
 5 datafile / .DATA - general data
 6 graffile / .GRAF - (not used)
 7 fotofile / .FOTO - (not used)
 8 securedir - (unknown)
```
Note that .TEXT files have a specific structure based around 1024-byte pages, and store runs
of leading spaces in compressed form (very useful for Pascal source).  See page IV-16 in the
Pascal 1.3 manual for a description.  "Untyped" files are discussed in chapter 10 (page III-156).

## Disk Structure ##

Blocks 0 and 1 are reserved as boot blocks.  The directory starts in block 2.  All disks have a
2048-byte directory spanning blocks 2 through 5 (inclusive), regardless of volume size.  Each
directory entry is 26 bytes long, providing space for 78 entries.  The first entry holds a volume
header, so a disk can hold up to 77 files.  (The Apple Pascal 1.3 manual warns that, while the
ProFile hard drive can be formatted as a Pascal volume, it will still be limited to 77 files.)

The volume directory entry is:
```
+$00 / 2: first block (always 0)
+$02 / 2: next block (first block after directory; always 6)
+$04 / 2: file type ($00)
+$06 / 8: volume name, prefixed with length byte
+$0e / 2: number of blocks in volume
+$10 / 2: number of files in directory
+$12 / 2: last access time (declared as "integer", not "daterec"; not sure what this is)
+$14 / 2: most recently set date value
+$16 / 4: (reserved)
```
A regular directory entry is:
```
+$00 / 2: file start block number
+$02 / 2: first block past end of file (i.e. last block + 1)
+$04 / 2: file type in bits 0-3, bit 15 used "for filer wildcards", rest reserved
+$06 /16: file name, prefixed with length byte
+$16 / 2: number of bytes used in last block
+$18 / 2: modification date
```
All multi-byte integers are stored in little-endian order.

Directory entries are packed together.  When an entry is deleted, the entries that follow are
moved up, and the unused entry is zeroed out.  Entries are not sorted by block number.

### Timestamps ###

The filesystem stores the date, but not the time.  The date is held in a 16-bit value:
```
 YYYYYYY DDDDD MMMM
```
Years are 0-99, starting in 1900.  Months are 1-12, days are 1-31.  A zero value indicates no date.

The Filer utility uses a date with the year 100 as a flag to indicate file creation in progress.
If the system finds a file with year=100, it will silently remove the file.
