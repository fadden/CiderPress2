# Unadorned Disk Image Files #

## Primary References ##

- Sector skew explanation: https://retrocomputing.stackexchange.com/a/15063/56
- _Beneath Apple DOS_ (4th printing), esp. pages 3-22 and 3-23

## General ##

There are two general categories: nibble images, which contain the raw disk nibbles as they
are read from a floppy disk, and block/sector images, which contain the data as it is visible
to user programs.

## Block/Sector Images ##

One of the most common ways to store an Apple II disk image is an "unadorned" file, meaning it
has no header or other identifying information.  The file is simply a collection of blocks or
sectors as they were read from the disk.  The file extensions ".po", ".do", and ".dsk" are
commonly used.  ".hdv" is sometimes used for hard drive images, and ".iso" is commonly used
for CD-ROM images.

The contents of the file could be something simple, like a floppy disk with DOS files, or a
multi-partition hard drive with multiple operating systems.  It could be completely custom.
The only telling feature is that the file's length will be a multiple of 256.  In all but one
case (a 13-sector disk in a ".d13" file), it will be a multiple of 512.

### DOS vs. ProDOS Order ###

(This section is only relevant for 5.25" floppy disk images.  Larger volumes are always created
as blocks in ascending order, and sectors in embedded DOS volumes map linearly to blocks.)

DOS 3.2 disks used a simple scheme: if you request sector 5, you get sector 5.  This caused
some performance issues when reading multiple sectors consecutively, because by the time you
finished processing sector 5, the start of sector 6 had already passed by.  This meant the
system had to wait most of a full rotation before the next sector could be read.

DOS 3.3 introduced software "sector skewing".  The sectors were physically written in the same
order, but the sector numbers were remapped in software to match the performance characteristics
of the operating system.  This requires a lookup table.  Utilities that generate disk images
start from track 0 sector 0 and count up, using the skewed sector numbers.

UCSD Pascal and ProDOS/SOS use a different sector skew.  (DOS uses "-2" interleave, ProDOS
uses "+2".)  ProDOS disk utilities like ShrinkIt start from block 0 and count up, using the
skewed sector numbers.

If a CP/M utility created a disk image, it would use a third skew ("+3") for its 1KB blocks.  In
practice you are unlikely to find an example of this.

Version 7.x of the Copy ][+ disk utility created ".img" files with 16 sectors in physical order.
The feature was somewhat obscure, and did not support compression, limiting its usefulness.  It
was removed from later versions.  The feature was activated by trying to perform a whole-disk
copy to a drive that held a larger ProDOS volume.

When the filesystem code requests a particular block or sector, it's making a request with the
logical block/sector number.  We need to map that request to a physical sector number, based on
whether the filesystem is block-oriented or sector-oriented.  That gets converted to a track
offset based on the layout of the disk image file.

The ugly part of the process is that file extensions are unreliable or ambiguous, e.g. ".dsk"
could be block- or sector-order.  Sometimes files are named with the wrong extension.  Not only
do we have to scan the disk to see which filesystem is present, we have to do it multiple times,
once for every possible sector ordering.

It should be noted that the sector order is unrelated to the contents of the disk.  Disk images
with DOS filesystems can be in ProDOS order, and vice-versa.

## Nibble Images ##

When creating an image of a non-standard (probably copy-protected) floppy disk, it can be
useful to create a nibble image instead.  These record the data read from the disk with minimal
processing.

There are two varieties of unadorned nibble image, both of which store 35 fixed-length tracks
from a 5.25" floppy disk.  ".nib" files record 6656 bytes per track, while the less-common
".nb2" files have 6384 bytes per track.  Both contain data read as octets from the disk
controller, with no attempt at preserving 9-bit or 10-bit bytes.  (The format is sometimes
referred to as NDO, or Nibble DOS Order, but that's misleading at best.)

Nibble images don't have the sector skew ambiguity that block/sector images have, because the
address fields on each sector identify the sector number.  While sector skewing must still be
handled, the file format is always "physical" order.

There may be some ambiguity in naming, e.g. sometimes they're called ".raw", or an image with
6384 bytes per track will be called ".nib", but the files must be 6656*35 or 6384*35 bytes
exactly.  The lengths are unlikely to be used for other formats.  (6656*35/512=455, so it could
be confused for a 227.5KB unadorned block image, but that's unlikley in practice.)

See the nibble file format notes for detailed information about the low-level track format.

The length of a track on a physical 5.25" disk is closer to 6384 than 6656.  This means that
a 6656-byte track either has very long gaps between sectors, or has some repeated data (hopefully
the former).

The length can present a problem for emulators when a floppy disk is formatted, because the ProDOS
format code will think the drive is spinning too slowly.  The approach used by AppleWin is to
write 336 zero bytes into a gap between sectors.  At first this seems like a problem, because
$00 isn't a valid disk byte, but in practice it just appears to the system as a 2696-bit
self-sync byte.
