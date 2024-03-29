# Pascal ProFile Manager (PPM) Partition #

## Primary Sources ##

 - ProDOS 8 TN #25 "Non-Standard Storage Types"
 - _Pascal ProFile Manager Manual_, https://archive.org/details/a2ppfmm/

## General ##

Apple published the "Pascal ProFile Manager" software to allow multiple UCSD Pascal volumes to be
stored on a disk formatted for ProDOS.  This was intended for use with Apple ProFile(tm) hard
drives.  It required Apple Pascal v1.2 or later.

Images of hard drives that use this system are rare.

## Layout ##

The PPM area is recorded in the ProDOS filesystem as a file called "PASCAL.AREA" in the root
directory.  It has file type PAS ($ef), and storage type of $4.  No other type of file uses
storage type 4.  The ProDOS "key pointer" is set to the first block of the PPM area, and the
"blocks used" value is set to the region size.

The first two blocks of the PPM area contain some header data and the volume map.  Information
is stored for 31 partitions and the PPM volume itself, which is considered volume zero.  If the
two blocks are combined into a 1KB buffer, the basic layout is:
```
+$000 / 256: volume info (8 bytes x 32)
+$100 / 512: volume description (16 bytes x 32)
+$300 / 256: cached volume names (8 bytes x 32)
```
Broken down further:
```
+$000 / 2: total size of the PPM region, in blocks (should match "blocks used" in dir entry)
+$002 / 2: number of volumes (1-31)
+$004 / 4: signature: "PPM", preceded by length byte
+$010 / 8: info for volume #1
  ...
+$0f8 / 8: info for volume #31
+$100 /16: description for volume #0 (not used)
+$110 /16: description for volume #1: ASCII string preceded by length byte
  ...
+$2f0 /16: description for volume #31
+$300 / 8: volume name of volume #0 (not used)
+$308 / 8: volume name of volume #1 (cached copy, read from actual volume)
  ...
+$3f8 / 8: volume name of volume #31
```
The volume info data is:
```
+$00 / 2: absolute start block of volume (within ProDOS disk, not within PPM)
+$02 / 2: length of volume, in blocks
+$04 / 1: default unit
+$05 / 1: write-protection flag (high bit)
+$06 / 2: old driver address (used when floppy drive unit numbers are assigned to PPM volumes)
```

It's important to note that the starting block for each partition is an absolute ProDOS filesystem
block number, not relative to the start of the PPM area.  This means that extracting the PPM area
from ProDOS into a separate file is not useful unless the block numbers are rewritten at the
same time.

Partitions appear to be stored in ascending order of starting block.  The PPM volume manager seems
to have made design choices that are similar to Apple Pascal's filesystem, so it's likely that
this is a requirement.  Deleted volumes are not represented in the directory.  The PPM volume
manager can "krunch" (defragment) space after Pascal volumes have been deleted.

The largest possible Pascal volume appears to be 16MB.  The PPM area can fill the ProDOS volume.
