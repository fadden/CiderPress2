# Apple II Disk Operating System Filesystem #

## Primary References ##

 - _Beneath Apple DOS_ (prefer 5th printing):
   https://archive.org/details/Beneath_Apple_DOS_alt/
 - The DOS Manual: https://archive.org/details/the-dos-manual

## General ##

Apple's Disk Operating System was created for use with the floppy disk drives it was developing.
There were three major revisions, numbered 3.1, 3.2, and 3.3.

Disks are arranged in tracks and sectors, which is common, but in an unusual move the disk
geometry is exposed to the filesystem.  (Most filesystems use sequential blocks, which are mapped
to physical disk locations at a lower level.)  A standard 5.25" floppy disk has 35 tracks, with a
fixed number of 256-byte sectors per track.  DOS 3.1/3.2 had 13 sectors per track (113KB), while
DOS 3.3 has 16 (140KB).  Some drives allowed up to 40 tracks, and DOS volumes embedded in other
storage can have up to 50 tracks of 32 sectors (400KB).

## Disk Structure ##

The Volume Table of Contents is stored on track 17, sector 0.  The rest of
track 17 typically holds the file entry catalog.  File locations are
identified via track/sector pairs, with track==0 indicating a non-entry.
This renders track 0 unusable for file storage.

Bootable floppies use tracks 0, 1, and 2 to store the DOS image.

VTOC layout:
```
+$00 / 1: (reserved; seems to hold 4 for DOS 3.3, 2 for DOS 3.2)
+$01 / 2: track/sector of first catalog sector (usually T=17 S=15)
+$03 / 1: DOS version used to format this disk (3 for DOS 3.3, 2 for DOS 3.2)
+$04 / 2: (reserved)
+$06 / 1: disk volume number (should be 1-254)
+$07 /32: (reserved)
+$27 / 1: max T/S pairs per T/S list sector (should be 122)
+$28 / 8: (reserved)
+$30 / 1: last track where sectors were allocated
+$31 / 1: direction of track allocation (should be +1 or -1)
+$32 / 2: (reserved)
+$34 / 1: number of tracks on this disk (usually 35, 40, or 50)
+$35 / 1: number of sectors on this disk (usually 13, 16, or 32)
+$36 / 2: number of bytes per sector (should be 256)
+$38 / 4: free sector bitmap for track 0 (a '1' bit means "free")
+$3c / 4: free sector bitmap for track 1
 ...
+$fc / 4: free sector bitmap for track 49
```
In the free sector bitmaps, a '1' bit means "free".  The entry for each track
can be viewed as a big-endian 32-bit value, in which the highest-numbered
sector is in the high bit.  The value $80 $00 $00 $00 would indicate that
only sector 15 was free on a 16-sector disk, or sector 31 on a 32-sector disk.

Catalog sector layout:
```
+$00 / 1: (reserved)
+$01 / 2: track/sector of next catalog sector; T=0 means end of catalog
+$03 / 8: (reserved)
+$0b /35: file entry #1
+$2e /35: file entry #2
+$51 /35: file entry #3
+$74 /35: file entry #4
+$97 /35: file entry #5
+$ba /35: file entry #6
+$dd /35: file entry #7
```

File entry layout:
```
+$00 / 2: track/sector of first track/sector list sector; T=0/255 are special
+$02 / 1: file type and flags
+$03 /30: file name (high-ASCII bytes, padded with spaces)
+$21 / 2: number of sectors required to store file
```
T=0 indicates an entry that has never been used.  When a file is deleted, the
original track is copied to the end of the filename (entry +$20), and the
track number is set to $ff.  This means that, on a correctly-formed disk, the
catalog scan can stop the first time an entry with T=0 is encountered.  (Some
disks have garbage entries that will appear if you *don't* stop at the first
unused entry.)

Filenames do not have an explicit length, but trailing spaces are ignored.
According to _The DOS Manual_, filenames must begin with a letter and may not
contain a comma.  In practice, the limitation appears to be on ASCII values
below $40, so "/FOO" and ":FOO" cause problems on the command line, but "@FOO"
and "^FOO" work.

Because DOS is expected to work on early systems, it is best if filenames are
entirely upper-case.  Names are sometimes stored with low-ASCII values for
special effects (inverse or flashing filenames), but these are inaccessible
from the command line.

File types and flags:
```
 $00: T - text file
 $01: I - Integer BASIC
 $02: A - Applesoft BASIC
 $04: B - binary
 $08: S - "special"?; effectively typeless
 $10: R - relocatable object module
 $20: A+ - undefined 'A' type
 $40: B+ - undefined 'B' type
 $80: flag, indicates file is locked
```

Track/sector list sectors:
```
+$00 / 1: (reserved)
+$01 / 2: track/sector of next T/S list sector; T=0 indicates end of list
+$03 / 2: (reserved)
+$05 / 2: sector offset in file of the first sector defined by this list
+$07 / 5: (reserved)
+$0c / 2: track/sector of 1st data sector (T=0 indicates "sparse" sector)
+$0e / 2: track/sector of 2nd data sector
 ...
+$fe-ff: track/sector of 122nd sector
```

Every file must have a valid T/S pointer in the catalog entry, which means
that the first sector of a file (the T/S list) will always exist.

## File Structure ##

DOS does not store the file's length in the catalog track.  The sector count
is there, but this is primarily for display.  Determining a file's actual
length may require scanning the file's contents.

 - T (text): sequential text files end when the first $00 is encountered.
Random-access text files end at the last sector in the last T/S list.
DOS text files use high ASCII, with CR ($8d) indicating end-of-line.
 - I/A (BASIC): the file starts with a 16-bit file length.
 - B (binary): the file starts with a 16-bit start address, followed by a
16-bit file length.  Some programs with a custom loader deliberately set
a length that was a small fraction of the full length of the file.
 - R (relocatable): this is described in Appendix E of the DOS Toolkit
_Apple 6502 Assembler/Editor_ (EDASM) manual.  There is a "length of code
image" field at +$04-05 that indicates the length of the file.  It's unclear
how widely used this format is.

Because of the way the file length is handled, anything that copies DOS files
must copy all sectors in the T/S list.  Preserving random access "holes" may
need to be done explicitly, rather than simply recording zero-filled blocks,
because a custom RWTS routine might expect some sectors to exist even if they
currently just hold zero.

Newly-created text files have an empty T/S list, and are only 1 sector long.

## Sparse Files ##

For sequential-access files, the file cannot be accessed past the point where
a T/S entry with T=0 is encounted.  If you try to BLOAD a 'B' file with a
sparse sector, the load will halt when the sector is reached.  For a
random-access text file, it's possible to seek to an arbitrary sector, so the
file's true length is determined by the last nonzero entry in the last
T/S list sector.

Unlike ProDOS under GS/OS, sectors are not un-allocated once allocated, i.e.
a file cannot become more sparse.

## 80-Track Disks ##

The drives used with the Basis 108 (an Apple II clone) were able to store data
on "half tracks", allowing a disk with 80 tracks of 16 sectors.  The VTOC was
modified so that each track used two bytes in the in-use bitmap, instead of 4.
([Example here](https://github.com/davidgiven/fluxengine/issues/645#issuecomment-1489205354).)
