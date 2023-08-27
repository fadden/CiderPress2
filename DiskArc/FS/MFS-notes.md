# Apple "Macintosh File System" (MFS) #

## Primary Sources ##

 - _Inside Macintosh, Volume II (1985)_ -- chapter 4, esp. page II-119+ describes the structure
   of MFS.
 - _Inside Macintosh, Volume IV_ (1986) -- page IV-89+ has some notes about 64K ROM features.
   Apparently the 128K ROM is when HFS was introduced.  It also talks about version numbers on
   files (and their lack of usefulness).  Page IV-160+ describes the MFS structure (appears to
   be a repeat of the Volume II chapter).
 - page IV-105 indicates that FInfo was created for MFS-era Finder, while FXInfo was added
   for HFS-era.
 - https://developer.apple.com/library/archive/samplecode/MFSLives/Introduction/Intro.html

## General ##

MFS was introduced with the original Macintosh in 1984.  It was replaced with HFS in 1986, with
the launch of the Macintosh Plus.  Support for MFS was included in the operating system until
Mac OS 8.1.

The Macintosh desktop provided an environment with files in folders, but that was implemented
entirely within the Finder.  The filesystem itself has a single directory.

Apple's 3.5" disk drives stored 524 bytes per block.  512 bytes were part of the logical block
available to the filesystem, while the additional 12 were accessible through the disk driver.
Files are stored in "allocation blocks", which are a multiple of 512 bytes.

## Disk Structure ##

Blocks 0 and 1 hold "system startup information" for startup disks.  On non-bootable disks, the
blocks were filled with zeroes.

Block 2 and 3 are the Master Directory "Block" (MDB), which is comprised of the 64-byte Volume
Information chunk and the Volume Allocation Block Map.  The Volume Information specifies how the
disk is laid out.  The block map is an array of 12-bit values that act as both a block-in-use
indicator and a link in a file's block list.

The MDB is usually followed by the file directory, which starts on block 4, and will be 12 blocks
long on a 400KB volume.  The directory's size is fixed.  The allocation blocks used to hold files
begin immediately after.  It's also possible to have the file directory created in the allocation
block area, though, in which case the blocks it lives on must be marked as in use in the block
map (using alloc block $fff).

A backup copy of the MDB blocks is stored at the end of the disk.

Given this structure, a 400KB floppy disk should look like:
```
block 0-1: system startup information
block 2-3: volume information (64 bytes) and block map (960 bytes)
block 4-15: file directory
block 16-797: allocation blocks with file contents
block 798-799: backup copy of MDB
```
With an allocation block size of 1KB, there will be 392 allocation units.  At 12 bits each,
that requires 588 bytes, which fits easily in the space available.  (If the allocation block
size were 512 bytes, we'd have 784 allocation units, which would need 1176 bytes.)

The Volume Information chunk is:
```
+$00 / 2: drSigWord - signature ($D2D7, 'RW' in high ASCII)
+$02 / 4: drCrDate - date/time of initialization
+$06 / 4: drLsBkUp - date/time of last backup
+$0a / 2: drAtrb - volume attributes
+$0c / 2: drNmFls - number of files in directory
+$0e / 2: drDrSt - first block of directory
+$10 / 2: drBlLen - length of directory, in blocks
+$12 / 2: drNmAlBlks - number of allocation blocks in volume
+$14 / 4: drAlBlkSiz - size of an allocation block, in bytes
+$18 / 4: drClpSiz - number of bytes to allocate at a time ("clump" size)
+$1c / 2: drAlBlSt - block number where file allocation blocks start
+$1e / 4: drNxtFNum - next unused file number
+$22 / 2: drFreeBks - number of unused allocation blocks
+$24 /28: drVN - volume name, starting with a length byte
```
All values are in big-endian order.

The `drAtrb` field has two bits defined: bit 7 is set if the volume is locked by hardware, bit 15
is set if the volume is locked by software.

The volume name uses the Macintosh character set.  Mac OS Roman may be assumed.  No restrictions
on characters are specified in the documentation.

### Volume Allocation Block Map ###

The block map is an array of 12-bit values, stored in big-endian order.  The bytes 0xAB 0xCD 0xEF
would become 0x0ABC and 0x0DEF.  Allocation block numbers 0, 1, and 0x0fff are reserved, so the
first entry in the table is for alloc block #2.  (This has nothing to do with disk block numbers
or the system boot area.)

The block map has one entry for each allocation block on the disk.  The size of the map is
a bit vague, as the documentation first says "the master directory 'block' always occupies two
blocks", and then later says it "continues for as many logical blocks as needed".  Of course, if
the disk initialization code ensures that the MDB always fits in two blocks, then the second
statement is always true.  In any event, a 1024-byte MDB can cover 640 allocation blocks.

The file directory entry provides the number of the first block in a file.  The block map entry
for that block holds the allocation block number of the next block in the file.  This provides
a chain of block numbers that ends when an entry with the value 1 is reached.

If a block is unused, the map entry will be zero.

### File Directory ###

MFS has a single directory for the entire volume.  Each entry has a variable size, 51 bytes plus
the filename.  Entries aren't allowed to cross block boundaries, so it's not necessary to hold
multiple catalog blocks in memory at once.  The start of each entry is aligned to a 16-bit word.

Each entry has the form:
```
+$00 / 1: flFlags - bit 7 = 1 if entry used; bit 0 = 1 if file locked
+$01 / 1: flTyp - version number (usually $00)
+$02 /16: flUsrWds - Finder information; includes file type and creator
+$12 / 4: flFlNum - unique file number
+$16 / 2: flStBlk - first allocation block of data fork (0 if none)
+$18 / 4: flLgLen - logical length of data fork
+$1c / 4: flPyLen - physical length of data fork
+$20 / 2: flRStBlk - first allocation block of rsrc fork (0 if none)
+$22 / 4: flRLgLen - logical length of resource fork
+$26 / 4: flRPyLen - physical length of resource fork
+$2a / 4: flCrDat - date/time of creation
+$2e / 4: flMdDat - date/time of last modification
+$32 /nn: flNam - filename, starting with a length byte
```
The filename uses the Macintosh character set, and may be up to 255 characters long.  No
restrictions on characters are mentioned in the documentation.  Since MFS is a flat filesystem,
even ':' should be allowed.

File numbers start at 1 and are never re-used within a volume.

The "logical" length is the actual length of the fork.  The "physical" length is the storage
required, and is always a multiple of the allocation block size.

Finding an entry for which `flFlags` does not have the high bit set would seem to indicate the
end of entries in that block.  In theory an entry could be deleted without being zeroed, and
the directory traversal code would then continue to process the bytes to determine the full
length of the deleted entry.  This would allow un-deletion.  The MFSLives project interprets
attributes==0 to mean that there are no further entries in the block, so that's the recommended
approach.

Renaming a file could necessitate relocating the entry to a different directory block.

Page IV-90 in _Inside Macintosh: Volume IV_ notes:
> The 64K ROM version of the File Manager allows file names of up to 255 characters.  File names
> should be contrained to 31 characters, however, to maintain compatibility with the 128K ROM
> version of the File Manager.

With regard to the `flTyp` version number field, it continues:
> The 64K ROM version of the File Manager also allows the specification of a version number
> to distinguish between different files with the same name.  Version numbers are generally
> set to 0, though, because the Resource Manager, Segment Loader, and Standard File Package
> won't operate on files with nonzero version numbers, and the Finder ignores version numbers.

### Timestamps ###

Timestamps are unsigned 32-bit values indicating the time in seconds since midnight on
Jan 1, 1904, in local time.  (This is identical to HFS.)
