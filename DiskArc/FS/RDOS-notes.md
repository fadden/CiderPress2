# RDOS Filesystem #

## Primary References ##

 - RDOS 2.1 disassembly
 - ProDOS RDOS implementation, https://fadden.com/apple2/unprotect.html

## General ##

The RDOS operating system was developed by Roland Gustafsson for
[Strategic Simulations, Inc.](https://en.wikipedia.org/wiki/Strategic_Simulations) (SSI),
a game development and publishing company.  SSI used the operating system on dozens of titles.
The OS needed very little memory, and could be used from Applesoft BASIC through an ampersand
interface.

The filesystem layout is similar to Apple Pascal, featuring a list of files described by a
start block and length.

There are two significant versions: RDOS 2.1 was distributed on 13-sector disks, and RDOS 3.3
was distributed on 16-sector disks.  There are also cracked copies of 13-sector disks that used
a simple approach: the sectors were copied to a 16-sector disk, and the OS was modified to use
16-sector I/O routines but only use the first 13 sectors on each track.

A later effort, ProDOS RDOS, converted the files to ProDOS and replace the OS entirely.  The
conversion program gave the formats the following labels:

 - `RDOS33` - 16-sector (same physical format as DOS 3.3), uses ProDOS sector skew
 - `RDOS32` - 13-sector (same physical format as DOS 3.2), uses physical sector skew
 - `RDOS3` - 13-sector layout on a 16-sector disk, uses physical sector skew

To avoid confusion with other documentation sources, I will continue to use the names here.

The disk sectors did use modified address headers, but were otherwise DOS-compatible.

## Disk Structure ##

The operating system lives on track 0.  RDOS32 disks had two different boot sectors to allow
the disk to be booted on systems that had been upgraded to support 16-sector disks.

The disk catalog lives on track 1.  On 13-sector disks it occupies sectors 0 through 10, with
sector 11 holding Applesoft "chain" code, and sector 12 holding the code that actually performs
the disk catalog.  On 16-sector disks the additional code is stored on track 0, so all of track 1
is available for file entries.

Each entry is 32 bytes:
```
+$00 /24: filename, high ASCII, padded with trailing spaces
+$18 / 1: file type, high ASCII 'A', 'B', 'T', or 'S'
+$19 / 1: number of 256-byte sectors used by this file
+$1a / 2: load address for 'B', not really used for 'A' and 'T'
+$1c / 2: file length in bytes (rounded up for 'T')
+$1e / 2: index of first sector
```
Two-byte integers are in little-endian byte order.

The sector index is a 16-bit value that starts in T0S0.  It works like a ProDOS block number, but
with 256-byte sectors.  Sector index 13 is either T0S13 for RDOS33, or T1S0 for RDOS32/RDOS3.
Files appear to be sorted by ascending sector index to simplify scanning for empty regions when
creating new files.

Filenames may include any character except double quotes, since that would interfere with the
ampersand-based argument passing, and may not have trailing spaces.

When a file is deleted, the first character of the filename is set to $80, and the file type is
set to $A0 (space).  If you create a new file, it will use the deleted file slot, and will
occupy the entire region that the previous file occupied.

The first entry on every disk spans the OS and catalog tracks.  On game disks it's called
`RDOS 2.1 COPYRIGHT 1981 ` or `RDOS 3.3 COPYRIGHT 1986 `, and on save-game disks created by
initialization code it's `SSI SAVE GAME DISK RDOS ` or ` >-SSI GAME SAVE DISK-< `.

### File Types ###

Files may be Applesoft BASIC, binary, or text.  Applesoft and binary are stored the same way they
would be on a ProDOS disk.

Text files are very similar to sequential text files on DOS 3.3: they're encoded in high ASCII,
use CR ($0d) for line breaks, and have a length that is rounded up to the nearest sector.  To
determine the actual length of a text file it's necessary to scan it for the first occurrence of
a $00 byte.  (When creating a text file, RDOS requires the program to pre-size it, and does not
track the actual length in the catalog.)

The catalog header on a newly-initialized RDOS 3.3 saved-game disk has type 'S'.

### Copy Protection ###

13-sector disks used a modified sector address header.  16-sector disks used different address
headers on odd/even tracks, and altered the address epilog bytes.  These changes were used
consistently across all titles.

These changes were easily handled by contemporary nibble copiers, so many games had a secondary
protection check.
