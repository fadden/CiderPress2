# DOS 3.3 on 800KB Floppies #

There are at least three different but functionally similar programs that put a pair of 400KB
DOS 3.3 volumes on an 800KB 3.5" disk.  These are effectively multi-partition disk formats,
rather than "hybrid" formats like DOS MASTER.

 1. UniDOS Plus v2.1 by MicroSPARC, Inc. -
    https://macgui.com/downloads/?file_id=36130 and
    https://www.apple.asimov.net/images/masters/3rd_party_dos/UniDOS%20Plus.pdf
 2. AmDOS 3.5 by Gary Little -
    https://www.apple.asimov.net/images/masters/3rd_party_dos/amdos.dsk
 3. OzDOS by Oz Data / Richard Bennett -
    https://www.apple.asimov.net/images/masters/3rd_party_dos/OzDOS10.zip

All three can create a bootable 3.5" disk.  Each DOS volume has 50 tracks and 32 sectors, which is
the maximum DOS allows.

Each requires alterations to DOS 3.3, as well as modifications to the FID file utility.

## UniDOS ##

There are two versions, "UniDOS 3.3" and "UniDOS PLUS".  Various improvements were made to the
software, but the disk layout didn't change.

The first volume is stored in blocks 0-799, the second in blocks 800-1599.  Each 512-byte disk
block holds two consecutive DOS sectors.

The catalog track is extended to fill all of track 17, and starts in T17 S31.  This allows it
to hold 217 file entries instead of 105.

## AmDOS ##

The software is completely different from UniDOS, but the disk layout is essentially the same.
For compatibility with programs that start reading from T17 S15 without checking the VTOC, the
disk catalog runs from sector 15 to 1, then 31 to 16.

## OzDOS ##

Instead of putting the volumes in separate block ranges, OzDOS splits individual blocks in half.
The first volume uses the first 256 bytes of each block, and the second volume uses the last
256 bytes.

OzDOS has some unusual features, e.g. if you boot an OzDOS disk while DOS 3.3 is already active,
it will patch the existing OS instead of overwriting it with its own code.  This allows OzDOS
to be used to augment "fast DOS" implementations like Pronto-DOS.
