# Apple II "Professional Disk Operating System" Filesystem #

## Primary References ##

 - _Beneath Apple ProDOS_ (Second Printing, March 1985; check the errata
   in _Supplement for 1.2/1.3_, page 152+)
   https://archive.org/details/Beneath_Apple_ProDOS_Alt/mode/1up
 - _ProDOS 8 Technical Reference Manual_: https://prodos8.com/docs/techref/
 - ProDOS 8 TN #25 "Non-Standard Storage Types"
 - ProDOS 8 TN #28 "ProDOS Dates -- 2000 and Beyond"
 - ProDOS 8 TN #30 "Sparse Station"
 - GS/OS TN #8 "Filenames With More Than CAPS and Numerals"

Also:
 - Apple II File Type Note $19/xxxx (AppleWorks document), section "Auxiliary
   Type Definitions", for AW's approach to lower-case flags

## General ##

The ProDOS filesystem was originally developed for the Sophisticated Operating System (SOS) on
the Apple /// computer.  The version used for ProDOS 8 is structurally identical, though some
extensions were made for GS/OS.

The filesystem is based on 512-byte blocks, identified by a 16-bit unsigned integer.  This
yields a maximum volume size of 65535 blocks, one short of 32MB.  (Some hard drive image files are
65536 blocks long; the last block is unused.)  Individual files have a maximum length of 2^24 - 1,
one byte shy of 16MB.

The filesystem is hierarchical in nature.  Disk directories appear in the filesystem, but are
structured differently from other files.

All files have a "key block" in their directory entry.  The meaning of the key block changes
for regular files as the file's length changes.  For a short "seedling" file (<= 512 bytes), the
key block holds the file contents.  For a medium-length "sapling" file (513 bytes to 128KB),
the key block is an index block that holds pointers to the data blocks.  For a large "tree" file
(128KB to 16MB), it holds pointers to index blocks.

Directory files are stored as a simple linear list of file entries.  The volume directory cannot
change size, but subdirectories are allowed to grow (but not shrink).

GS/OS added a new storage type, called "extended", that has a data fork and a resource fork,
similar to HFS.  The key block is an "extended information block", which holds information like
storage type and length for each fork.  The block can also hold Macintosh Finder information,
such as the HFS creator and file type.

The filesystem supports "sparse" files.  If the block number listed in an index or master index
block is zero, any data read from the block in question will simply be zero-filled.  In ProDOS 8,
sparse files can be created by seeking past the end of the file and then writing data.  In GS/OS,
any block that is filled entirely with zeroes is stored as a sparse entry.  The only exception is
the very first data block in the file, which is never sparse.

GS/OS repurposed some of the fields to allow lower-case filenames.  For backward compatibility,
the names are still stored as upper case, but applications that are aware of the case flags can
choose to display the names as mixed-case.  The filesystem is thus case-insensitive but
case-preserving.  AppleWorks files use a parallel scheme, storing the case flags in the auxiliary
type field.

## Disk Structure ##

The contents of the first 3 512-byte blocks are defined by the system.  Block 0 holds the ProDOS
boot loader, and block 1 is usually empty but can hold a SOS boot loader (for the Apple ///).  The
volume directory begins at block 2, and is usually 4 blocks long, but could be longer or shorter
and doesn't have to occupy contiguous blocks.  The blocks-in-use bitmap usually appears right
after the volume directory, in block 6, and may occupy up to 16 blocks.

All directories, including the volume directory, begin with previous/next block numbers to
facilitate bidirectional directory traversal:
```
+$00 / 2: previous dir block number
+$02 / 2: next dir block number
```
A block number of zero is used to indicate the end of the list.

Each block holds a whole number of directories entries, i.e. entries do not straddle block
boundaries.  The length of each entry is defined by the directory header, but is commonly 39
bytes, allowing 13 entries per block.

The first entry in each directory is a special header that has the same size and general layout
as a standard entry, but has some different fields.  The volume directory header is different
from that in subdirectories.

Volume directory header:
```
+$00 / 1: storage type / name length ($Fx)
+$01 /15: volume name (A-Z, 0-9, '.', must start with letter)
+$10 / 2: (reserved, should be zeroes)
+$12 / 4: (undocumented? GS/OS feature) modification date/time
+$16 / 2: lower-case flags (see TN.GSOS.008)
+$18 / 4: creation date/time of this volume
+$1c / 2: version/min_version (min version must be 0 or GS/OS gets upset?)
+$1e / 1: access flags
+$1f / 1: directory entry length (usually $27)
+$20 / 1: entries per directory block (usually $200/$27 = $0d)
+$21 / 2: number of active entries in volume directory (not including header)
+$23 / 2: volume bitmap start block
+$25 / 2: total blocks in volume
```

Directory header:
```
+$00 / 1: storage type / name length ($Ex)
+$01 /15: subdirectory name (redundant)
+$10 / 1: (reserved, must contain $75 for P8, or $76 for GS/OS)
+$11 / 7: (reserved, should be zeroes)
+$18 / 4: creation date/time of this directory (redundant, not updated)
+$1c / 2: version/min-version (not used for lower-case flags)
+$1e / 1: access flags (redundant, not updated)
+$1f / 1: directory entry length (usually $27)
+$20 / 1: entries per directory block (usually $200/$27 = $0d)
+$21 / 2: number of active entries in directory (not including header)
+$23 / 2: parent pointer (block of directory with entry for this dir; NOT key block)
+$25 / 1: parent entry number (entry number within parent directory block, 1-N)
+$26 / 1: parent entry length (length of entries in parent dir, should be $27)
```

Regular directory entry:
```
+$00 / 1: storage type / name length
+$01 /15: file name (A-Z, 0-9, '.', must start with letter)
+$10 / 1: file type
+$11 / 2: key pointer (block number where storage begins)
+$13 / 2: blocks used
+$15 / 3: EOF
+$18 / 4: creation date/time
+$1c / 2: version/min-version -OR- lower-case flags (see TN.GSOS.008)
+$1e / 1: access flags
+$1f / 2: aux type
+$21 / 4: modification date/time
+$25 / 2: header pointer (key block number of directory that holds this file)
```

Storage types (described in more detail later):
```
 $00: deleted entry
 $01: seedling
 $02: sapling
 $03: tree
 $04: Pascal area on ProFile hard disk drive (described in ProDOS 8 TN #25)
 $05: GS/OS forked file
 $0d: subdirectory
 $0e: subdirectory header entry
 $0f: volume directory header entry
```
Setting the storage type to zero is insufficient when deleting a file.  The entire byte, which
also includes the filename length, must be zeroed.

### Fields ###

Filenames are case-insensitive but (with GS/OS extensions) case-preserving.

Access flags (8-bit value):
```
 D R B - - I W R
 $80: destroy-enabled
 $40: rename-enabled
 $20: backup-needed
 $10: (reserved)
 $08: (reserved)
 $04: file-invisible (GS/OS addition)
 $02: write-enabled
 $01: read-enabled
```

Date and time (two 16-bit values):
```
 YYYYYYY MMMM DDDDD
 000 HHHHH 00 MMMMMM
```
Years start in 1900, with some weird rules.  Months are 1-12, days 1-31,
hours 0-23, and minutes 0-59.  Seconds are not represented.  Timestamps are
in local time.

The year field holds 0-127.  The official (ProDOS Tech Note #28) mapping is:
 - 0-39 = 2000-2039
 - 40-99 = 1940-1999
 - 100-127 = unused

However, the tech note says, "Apple II and Apple IIgs System Software does not
currently reflect this definition".  In practice, many ProDOS utilities seem
to work best when 100 is used for Y2K which suggests that it might be better
to use:
 - 0-39 = 2000-2039
 - 40-99 = 1940-1999
 - 100-127 = 2000-2027

See the ProDOS section in the [TimeStamp code](../../CommonUtil/TimeStamp.cs)
for the details of how this is handled.

## Data File Structure ##

The "storage type" field in the directory entry specifies the file structure.  There are
three basic types of data storage file, determined by the file length:

 - [0,512]: "seedling".  The key block holds the complete file contents.  This block is
   always allocated, even if the file has zero length.
 - [513,131072]: "sapling". The key block is an index block that holds up to 256 pointers to
   data blocks.
 - [131073,16777215]: "tree". The key block is a "master" index block that holds up to 128
   pointers to index blocks.  (It can be no more than half full because of the 16MB length limit.)

When a file grows large enough to expand, a new key block is allocated.  The seedling key block
becomes the first data block listed in the new index; the sapling index block becomes the first
index listed in the new master index.  When a file is truncated, the process is reversed.
The index block structure always spans the entire file.  For example, if you create a new file and
use the `SET_EOF` MLI call to set its length to 131073, the file becomes a 3-block tree file.
(The first block of the file is always allocated, so there's one for that, one for the index block
that points to it, and one for the master index block.)

Index blocks and master index blocks hold a series of 16-bit block numbers.  The values are
stored with the low byte in the first half of the block, and the high byte in the second half
of the block.  For example, a pointer to the first data block in a sapling file can be obtained by
combining bytes $00 and $80 from the key block.  Zeroed entries in index and master index blocks
indicate sparse sections of the file, which are treated as blocks full of $00 bytes.  Unused
entries in index blocks should be cleared to zero.

### Extended Files ###

The key block for forked files points to an extended key block entry.  The
block has "mini-entries" for each fork, with data at +$0000 and rsrc at
+$0100, plus some optional HFS FInfo/FXInfo data at +$0008.
```
+$00 / 1: storage type for fork (must be 1/2/3)
+$01 / 2: key block
+$03 / 2: blocks used
+$05 / 3: EOF
```

There may be two 18-byte entries with the Mac HFS finder info, immediately
following the data fork data.  This feature was added to the ProDOS FST in
GS/OS System 6.0.  The format is:
```
 +$08 / 1: size of first entry (must be 18)
 +$09 / 1: type of entry (1 for FInfo, 2 for FXInfo)
 +$0a /16: 16 bytes of Finder data
 +$1a / 1: size of second entry (must be 18)
 +$1b / 1: type of entry (1 for FInfo, 2 for FXInfo)
 +$1c /16: 16 bytes of Finder data
```
The ProDOS FST creates both, but the software for the Apple //e card for the
Mac LC may create only one.  The AppleShare FST uses the same format as HFS.

The contents of "FInfo" and "xFInfo" aren't detailed by technote #25, but
they are defined in _Inside Macintosh: Macintosh Toolbox Essentials_,
starting on page 7-47:
```
TYPE FInfo = RECORD
  fdType:     OSType;     {file type}
  fdCreator:  OSType;     {file creator}
  fdFlags:    Integer;    {Finder flags}
  fdLocation: Point;      {file's location in window}
  fdFldr:     Integer;    {directory that contains file}
END;
TYPE FXInfo = RECORD
  fdIconID:   Integer;    {icon ID}
  fdUnused:   ARRAY[1..3] OF Integer; {unused but reserved}
  fdScript:   SignedByte; {script flag and code}
  fdXFlags:   SignedByte; {reserved}
  fdComment:  Integer;    {comment ID}
  fdPutAway:  LongInt;    {home directory ID}
END;
```

In the HFS definitions, OSType/LongInt/Point are 4 bytes, Integer is 2 bytes,
SignedByte is 1 byte, so these are 16 bytes each.  Under GS/OS, these values
are accessed through the "option list" parameter on certain calls.  Some tests
with GS/OS showed the file type and creator were propagated, but the other
fields were zeroed out.

Most of the fields should NOT be preserved when a file is copied between
volumes.  For example, the pointer to the parent directory will almost
certainly be wrong.  The file type and creator type, and perhaps the Finder
flags, are the only things worth keeping.

(The discussion starting on page 5 of _GS/OS AppleShare File System Translator
External ERS_ for System 6.0 may also be helpful.)

## Volume Block Allocation Bitmap ##

The bitmap usually starts on block 6, immediately after the volume directory,
but that's not mandatory.  One bit is assigned for every block on the volume;
with 8*512=4096 bits per block, the bitmap will span at most
ceil(65535/4096)=16 blocks.

Each byte holds the bits for 8 consecutive blocks, with the lowest-numbered
block in the high bit.  Bits are set for unallocated blocks, so a full disk
will have nothing but zeroes.

Blocks are generally allocated in ascending order, with no regard for disk
geometry.

## Embedded Volumes ##

Disk volumes from UCSD Pascal can be embedded in a ProDOS disk, using storage
type $04.  This was implemented by Apple's *Pascal ProFile Manager* for use
with ProFile hard drives, and is referred to as [PPM](../Multi/PPM-notes.md).

Multiple DOS 3.3 volumes can be embedded in a ProDOS disk using Glen Bredon's
DOS MASTER.  See the notes on [hybrid disks](Hybrid-notes.md) for more details.

## Miscellaneous ##

A change list for ProDOS v1.0 through v2.0.1 can be found in ProDOS 8 TN #23,
"ProDOS 8 Changes and Minutia".

ProDOS 8 TN #30 ("Sparse Station") uses the following example as a way to
create a very large file with minimal data in it:

  `BSAVE SPARSE.FILE,A$300,L$1,B$FFFFFF`

This command works, but it should actually fail.  The maximum length of a file
is $ffffff, which means the last byte you can actually write is at $fffffe.
ProDOS 8 allows you to read and write the last byte in the last block, which
would make the file's length $1000000.  GS/OS doesn't seem to allow this.

It gets weirder:
  https://groups.google.com/g/comp.sys.apple2/c/Rt0_rAAN2CA/m/arII-OpSBQAJ
