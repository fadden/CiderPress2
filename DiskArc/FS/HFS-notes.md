﻿# Apple Macintosh "Hierarchical File System" (HFS) #

## Primary References ##

- _Inside Macintosh: Files_, chapter 2 (esp. p. 2-52+)
- _Inside Macintosh: Macintosh Toolbox Essentials_ (esp. p. 7-47+)

Additional resources:
- https://www.savagetaylor.com/2018/01/01/mac-os-standard-format-specifications-apple-kb-article-8647/
  (general overview)
- https://opensource.apple.com/source/xnu/xnu-2050.18.24/bsd/hfs/hfs_format.h or
  https://opensource.apple.com/source/hfs/hfs-366.1.1/core/hfs_format.h.auto.html
  (data structures and constants for HFS and HFS+)
- HFS/HFS+ doc:
  https://github.com/libyal/libfshfs/blob/main/documentation/Hierarchical%20File%20System%20(HFS).asciidoc
- libhfs project: https://www.mars.org/home/rob/proj/hfs/
- Linux kernel: https://github.com/torvalds/linux/tree/master/fs/hfs
- https://developer.apple.com/library/archive/documentation/mac/Files/Files-99.html
  (2-52 in IM:Files as HTML)
- https://developer.apple.com/library/archive/technotes/tn/tn1150.html (HFS+ info, some useful)

The _Inside Macintosh: Files_ chapter does a nice job of explaining how things are laid out on
disk, but falls a bit short when it comes to explaining how some of the pieces fit together.
This document summarizes the general information and then details the recommended algorithms.

## General ##

The Hierarchical File System was developed by Apple Computer for use on the Macintosh line of
computers.  It was introduced in 1986 with the launch of the Macintosh plus, as a successor to
MFS, and was designed to work with floppy disks and hard drives.  It has also been used to
distribute software on CD-ROM.  Support for HFS volumes was dropped in macOS 10.15.

HFS disks have 512-byte "logical blocks", but files are made up of "allocation blocks", which are
one or more consecutive logical blocks.  A volume can't have more than 65535 allocation blocks,
so for volumes over 32MB the size of the allocation blocks is increased.

Files have a "physical EOF", equal to the size of the storage allocated to the file, and a
"logical EOF", based on the actual contents of the file.  Sparse files aren't allowed, so
setting the logical EOF larger than the physical EOF requires new storage to be allocated.
(Doing so is encouraged in situations where you know the file size ahead of time, because it
increases the likelihood that allocated blocks will be contiguous.)

As files expand, they may briefly be given more storage than they actually need, according
to the volume's "clump size".  A "clump" is a group of contiguous allocation blocks that are
allocated together to reduce fragmentation.  The file may be trimmed when it's closed.

Files on the Macintosh can be identified by a "File System Specification" triplet: volume
reference number, directory number, and file name.  (The use of full pathnames is discouraged.)

Files and directories can be uniquely identified with a 32-bit integer, based on the "catalog
node ID" (CNID).  File and directory IDs exist in the same name space but are referred to
independently in the documentation as "directory ID" and "file ID".  The first 16 CNIDs are
reserved by Apple.  The defined values are:
  0 invalid (used as an argument in certain situations)
  1 (fsRtParID) is the root directory's parent (which doesn't actually exist)
  2 (fsRtDirID) is the root directory
  3 file number of the extents file
  4 file number of the catalog file
  5 file number of the bad allocation block file

With regard to CNIDs, the documentation notes (IM:Files page 2-25): "once an ID has been
assigned to a file, that number is not reused even after the file has been deleted".  It's
not clear how that's maintained, although the time required to create 4 billion files might
simply be assumed to exceed the useful lifespan of hard drives built in the late 1980s.  The
IDs appear to be issued serially, so if a filesystem rolled the counter over it would be
necessary to pause and renumber all directories and files on the drive.

Directories are not files.  They have attributes, like modification date/time, but hold no data.

Volume names are 1-27 characters long, and may contain any 8-bit value except colon (':', $3a).
File names are 1-31 characters long.  Both are treated as case-preserving but case-insensitive.
Non-printing (control) characters are allowed but discouraged, as values such as 0x00 can cause
problems when using the C interfaces and don't work on AppleShare servers.

Filenames (except for the volume name) are mostly stored in variable-length structures, and
start with a length byte.  In theory this could allow filenames to be up to 255 characters long,
but in practice the catalog file keys used in index blocks have a fixed size.  (Some online
resources claim that HFS supports 255-character filenames.  They are likely confusing HFS with
MFS, which did support the longer names.)

Officially, names starting with a ':' are partial paths, while names that don't start with a
colon are full paths, in which the first component is the volume name.

## Disk Layout ##

Logical blocks 0/1 are used for booting (see IM:Files page 2-57 for details).  Logical block 2
holds the "master directory block" (MDB), which specifies volume parameters and the locations
of other system structures, like the volume bitmap.  In practice, the volume bitmap always
starts in block 3.  A copy of the MDB is stored in the next-to-last block in the volume, and
updated when the system catalog or extents overflow file expands.  The idea is to have a copy
of the MDB with the structural information needed to reconstruct the volume if the primary MDB is
trashed.  Writing to it infrequently reduces the possibility that both blocks will be damaged.
The MDB is sometimes referred to as a "volume information block", or VIB.

The very last block of the hard drive is left empty.  One source says, "it is used to check
for bad sections of the hard disk".  The logical blocks between the regions at the start and
end become the allocation blocks.

The volume bitmap is used to track the status of the allocation blocks.  The size of the bitmap
depends on the number of allocation blocks in the volume.  Allocation blocks are allocated in
sequential groups, called "extents"; the first three extents for each file are specified in its
directory entry.  If needed, additional extents are kept in the "extents overflow" file.

The filesystem recognizes the possibility of being unmounted in a "dirty" state, perhaps due to
power loss.  A bit in the MDB is cleared when the volume is mounted, and set when it's
unmounted.  The OS tended to flush the MDB and volume bitmap infrequently, so if the bit is
clear when the volume is mounted, the OS will perform "free-space scavenging".  This regenerates
the volume bitmap by walking through the list of extents, and verifies that the "next CNID"
value in the MDB is actually higher than the highest CNID found in the catalog file.

Bad blocks may be detected during disk initialization.  The system can perform "bad block
sparing", adding extents associated with a special file (CNID #5) that isn't included in the
catalog.  (See IM:Files chapter 5, "Disk Initialization Manager".)

The system maintains two special files as B*-trees: the catalog file, and the extents overflow
file.  The catalog file lists every file and directory on the volume.

### B* Trees ###

The B-tree data structure was invented to provide a balanced tree structure in an environment
with fixed-size containers, such as disk blocks.  The basic idea is that each disk block is
a "node", and nodes are arranged in a tree structure.  A node holds multiple records in sorted
order, where each record is a combination of a search key and a pointer to a lower-level node.
Nodes to the left on the same level hold smaller values, while nodes to the right hold larger
values.  Finding an item in the tree requires traversing a relatively short and broad tree,
scanning the contents of each node to find a match or an appropriate child reference.

When a new record is added but the appropriate node is full, the node is split into two.  A
new record that points to the new node is added to the node's parent.  If the parent node is
full, the parent node is also split.  If the parent is the root, a new level is added to the
tree.  Removal of a record is similar: if a node is empty (or nearly empty), the contents are
merged with an adjacent node, and so on up the tree, potentially removing a level at the top.

The documentation consistently refers to the data structure as a "B*-tree", but it more closely
resembles a B+-tree as described here: https://en.wikipedia.org/wiki/B%2B_tree

HFS B*-tree files use 512 bytes per node, which map directly to logical disk blocks.  (While
trees are managed as a collection of allocation blocks, the size of an allocation block has no
bearing on the size of a node.)  The first node (node 0) holds a special "header node" that
describes the tree.  This is NOT the root of the tree; rather, it points to the root.
Additional nodes are allocated sequentially, with an in-use bitmap to track them.

Nodes in the middle of the tree are called "index nodes", while those at the bottom are called
"leaf nodes".  Unlike the classic B-tree definition, records in index nodes just point to other
nodes, and all data is stored in the leaves.  Each record starts with a key, whose definition
is specific to the tree type.  Records in index nodes are simply the key followed by a 32-bit
node index, pointing to the node at the next level down.

There are also "map nodes", linked in a list from the header node, that are used to extend the
in-use bitmap.

The contents of the records are specific to each B*-tree.  The catalog file has Catalog File Keys
and Catalog File Data Records, while the extents overflow file has Extent Keys and Extent
Data Records.  Some records are fixed in size, others are variable.

There is no fixed minimum or maximum number of records for a node.  A single node could hold 35
extent overflow index records, but only 3 catalog file data records.

### Catalog File ###

Catalog keys are formed by combining the parent directory CNID with the filename.  The keys are
sorted primarily by parent CNID, which ensures that all files and directories that live in a
given directory are grouped together in the tree leaves.  The filename sort is generally
case-insensitive alphabetical, but has specific handling for certain characters, so sorting is
most easily done with a table.  (The exact sort order is not documented in the standard references,
but the table can be extracted from Apple's HFS implementations.)

There are four types of data records in the catalog: one for files, one for directories, and two
"thread" records that connect a file or directory with its parent.  All records use the same
key (which is a combination of the filename and the parent CNID), although index records use
a fixed-length filename field while data records use a variable-length field, and thread records
have an empty filename.  The purpose of the thread records is to provide a way to get the full
key for the parent of a given child, so the data includes the parent's CNID and filename.

When a directory record is created, a directory thread record is also created.  The directory
thread is used to traverse the tree upwards.  For example, suppose a folder called "Inner"
is created inside "Outer".  Outer has CNID 18, Inner has CNID 19.  The search key for Inner
is "18/Inner", i.e. the parent directory's CNID combined with the directory's name.  To find
the parent (Outer), we need to find a directory with CNID 18, but there's no fast way to search
for an arbitrary CNID: the keys in the tree all have the *parent* CNID.  So instead we look for
a directory thread record with key "18/" (thread record keys have an empty filename), which yields
a record with the data field CNID=2 name="Outer".  This allows us to find "2/Outer" in the tree.
(CNID 2 is reserved for the volume directory.)

The documentation is unclear on whether directory threads *must* exist for every directory.
Assuming that they do, e.g. to find the volume directory, or get the first entry in a
subdirectory, may be unsafe.  However, libhfs and Linux HFS seem to make this assumption with
no ill effects, and it turns out to be convenient.

File threads are defined but don't appear to be created by Mac OS, GS/OS or libhfs, though they
are created by the Linux HFS implementation.  They would be helpful if files were specified
solely by CNID, but in practice the Macintosh OS uses an "FSSpec" (IM:F 2-86), which includes
the directory CNID and filename -- exactly what's needed to form a catalog file key.

The volume (root) directory uses CNID 2, with the volume name as the filename.  The search key
for the volume directory is "1/<VolumeName>"; it's the only record with CNID 1.  Because the keys
are sorted primarily by CNID, the entry for the volume directory always appears first in the
leaves.  (It's also possible to find the root directory by searching for its thread, using
key "2/".  This works even if the MDB's volume name is out of sync, but assumes the directory
thread exists.)

Some things to note: directories are simply records in the tree, with no additional storage
attached.  A volume can't have more files than it has allocation blocks, but directories
(and empty files) do not count against that limit.

### Extents Overflow File ###

Extents overflow keys are a combination of the file fork, the file CNID, and an allocation block
index (*not* an allocation block number).  For example, if a file fills up the three extents
in the catalog record with two allocation blocks each, then the allocation block index in the
first extent record in the overflow file will be 6.  The CNID is the primary sort key, followed
by the fork.

The extent record data is an ExtDataRec, which is an array of three ExtDescriptors, which are
simply the starting allocation block and a count.  Allocation block numbers and counts are 16-bit
values, so extent records are 12 bytes long.

In general, the B*-tree files may be extended in the same way as normal files.  The extents
overflow file itself may not be extended, to avoid the possibility of an extent descriptor
ending up in the part of the file it describes.  (See ExtendFileC() function in the dfalib
sources in fsck.hfs; it returns fxOvFlErr for this situation.)

Files that are written all at once on a clean disk could have a single extent, stored in the
catalog file entry, regardless of the file's size.  It's common for an HFS CD-ROM to have an
empty extents overflow file because the image was created by copying fully-formed files onto
a new volume.  A file that grows gradually over time, interspersed with other file activity,
could very well have one extent per allocation block.

### File Algorithms ###

**Find root directory:**

There are at least three ways to find the directory record for the root:

 1. Look for "1/<VolumeName>", where <VolumeName> comes from the MDB.  This works unless the
    MDB is out of sync, in which case the volume becomes unreadable.
 2. Get the first record from the first leaf node of the catalog tree.  Sounds convenient, but
    the code needed to do this isn't useful for anything else.
 3. Look for a directory thread with key "2/", and use the data in the thread record to form the
    key.  Assumes the directory thread exists.

**Full tree traversal:**

Traverse all nodes in the tree, with no specific ordering requirement.  (The contents of
directories will be grouped together, but the ordering of directories is determined by CNID.)

Because all data is in the leaf nodes, and all nodes at a given level of the tree are part of
a singly-linked list, it's possible to traverse the entire catalog or list of overflow
extents by:

 1. Find the leaf node with the root directory.  It's guaranteed to be the first entry in the
    leftmost leaf, because it has a CNID of 2.
 2. Walk the list of leaf nodes, following the forward link (ndFLink) field, processing all
    records in each node.

Traversing the catalog file in this way reads each leaf node only once, and avoids reading
any index blocks, which is why the Mac OS file search feature could be much faster than an
explicit recursive search through the directory structure.

Bear in mind that files can be moved, so a file's CNID may be smaller than that of its parent
directory.

**Directory traversal:**

Given the search key for a directory, find all the files in it (opendir/readdir).

 1. Get the directory entry, and extract the directory's CNID.
 2. Form a key with the CNID and an empty filename to find the directory thread.  The thread
    record is the first record in the tree with the directory's CNID, and is immediately
    followed by all files and directories that use the target directory as parent.
 3. Walk forward through the leaf node chain, examining all records.  Stop when a record with a
    different CNID in the key is found.

**Pathname search:**

Given a pathname, find the associated file.

 1. Create a key with the root directory CNID (2) and the first pathname component.  Search
    for the entry in the catalog tree.
 2. Loop, using the CNID from the record and the next pathname component.

### Tree Algorithms ###

Index nodes hold a collection of keys, with pointers to index or leaf nodes.  The contents of
the linked node have keys that are greater than or equal to the key in the index record.

When inserting a new record, it will (almost) always be added to the right of an existing
record.  In the catalog tree, which uses keys sorted by parent CNID, the root directory sits to
the left of everything because it is the child of CNID 1.  In the extents tree, it is possible to
need to insert a node to the left of all previous nodes.  When this happens, it is necessary to
walk up the tree and rewrite the index records to use the newly-inserted key.

**Insert node:**

 - If root node does not exist, create it.

 - Search tree to find leaf node where record belongs.  If key already exists, error.
 - If leaf has enough free space, add new record.  If the new record is the smallest,
   replace the key in the parent node (this may need to propagate all the way up).  Done.
 - Otherwise, split leaf node in half, based on the size of the records.  The goal is to
   have an even number of *bytes* used in both nodes, not an even number of *records*.  Keep
   a pointer to the key for the first record in the right node.
   - Optimization: factor the size and placement of the new record into the split, to ensure
     that the post-split nodes are evenly distributed.  Not strictly necessary so long as a node
     can hold at least 3 records.
   - Add the new record to whichever node is appropriate.  If it's now the first record of the
     left node, update the key in the parent.
   - Update the forward/backward node links.
   - Add a new index record to the parent, with the key of the first record in the right node.
     If there isn't enough room, loop to split parent.

**Delete node:**

 - Search tree to find leaf node with record.  If not found, error.
 - Delete record, and compact node.  If node isn't empty, we're done.
   - Optimization: if node is barely used, and one if its neighbors is similarly empty, combine
     the contents of the nodes.  This can improve record density for trees where many records
     were deleted.  If we only merge into the node on the left, then merging a node is identical
     to deleting a node as far as the upward tree maintenance is concerned.
 - If node is empty, delete it.  Update the forward/backward links.
   - Remove link to node from index record in parent.  If this empties the parent node, loop
     to remove that node as well.

 - If last record in root node is deleted, remove the root node, and update the header.

## File Attributes ##

Files have a 4-byte creator and a 4-byte file type.  These are usually human-readable, e.g. a
text file might use 'TEXT' (0x54455854), but the set of values is not restricted.

Preservation of ProDOS file types is described in Technical Note PT515
"Apple File Exchange Q&As", and in slightly more detail on pages 335-336 of
Byte Works' _Programmer's Reference for System 6.0_, which seems to come from
page 4 of _GS/OS AppleShare File System Translator External ERS_.

Timestamps are unsigned 32-bit values indicating the time in seconds since midnight on
Jan 1, 1904, in local time.  The timestamp will roll over on Feb 6, 2040.

## Important Data Structures ##

The structure and field names were originally published in Apple header files and documentation,
notably _Inside Macintosh: Files_.  The `hfs_format.h` header file has revised versions, in which
some fields were deprecated or repurposed.  Many fields were renamed.  Both forms are listed here.

MDB / HFSMasterDirectoryBlock (162 bytes)
```
+$00 / 2: drSigWord / signature ($4244 "BD", apparently for "Big Disk")
+$02 / 4: drCrDate / createDate
+$06 / 4: drLsMod / modifyDate
+$0a / 2: drAtrb / attributes
+$0c / 2: drNmFls / fileCount
+$0e / 2: drVBMSt
+$10 / 2: drAllocPtr / nextAllocation
+$12 / 2: drNmAlBlks / totalBlocks
+$14 / 4: drAlBlkSiz / blockSize
+$18 / 4: drClpSiz / clumpSize
+$1c / 2: drAlBlSt
+$1e / 4: drNxtCNID / nextCatalogID
+$22 / 2: drFreeBks / freeBlocks
+$24 /28: drVN (volume name Pascal string, end padded with zeroes)
+$40 / 4: drVolBkUp / backupDate
+$44 / 2: drVSeqNum
+$46 / 4: drWrCnt / writeCount
+$4a / 2: drXTClpSiz
+$4e / 4: drCTClpSiz
+$52 / 2: drNmRtDirs
+$54 / 4: drFilCnt
+$58 / 4: drDirCnt
+$5c /32: drFndrInfo[8 * long] / finderInfo[32 * byte]
+$7c / 2: drVCSize -> drEmbedSigWord
+$7e / 4: drVBMCSize/drCtlCSize -> drEmbedExtent
+$82 / 4: drXTFlSize
+$86 /12: drXTExtRec
+$92 / 4: drCTFlSize
+$96 /12: drCTExtRec
```

ExtDescriptor / HFSExtentDescriptor (4 bytes)
```
+$00 / 2: xdrStABN / startBlock
+$02 / 2: xdrNumAblks / blockCount
```

ExtDataRec / HFSExtentRecord (12 bytes)
```
+$00 /12: array of HFSExtentDescriptor[3]
```

NodeDescriptor / BTNodeDescriptor (14 bytes)
```
+$00 / 4: ndFLink / fLink
+$04 / 4: ndBLink / bLink
+$08 / 1: ndType / kind
+$09 / 1: ndNHeight / height
+$0a / 2: ndNRecs / numRecords
+$0c / 2: ndResv2 / reserved
```

BTHdrRec / BTHeaderRec (106 bytes, at +$0e-77 in header node)
```
+$00 / 2 ($0e): bthDepth / treeDepth
+$02 / 4 ($10): bthRoot / rootNode
+$06 / 4 ($14): bthNRecs / leafRecords
+$0a / 4 ($18): bthFNode / firstLeafNode
+$0e / 4 ($1c): bthLNode / lastLeafNode
+$12 / 2 ($20): bthNodeSize / nodeSize
+$14 / 2 ($22): bthKeyLen / maxKeyLength
+$16 / 4 ($24): bthNNodes / totalNodes
+$1a / 4 ($28): bthFree / freeNodes
+$1e /76 ($2c): bthResv / reserved
```

CatDataDirRec / HFSCatalogFolder (70 bytes)
```
+$00 / 2: cdrType+cdrResrv2 / recordType
+$02 / 2: dirFlags / flags
+$04 / 2: dirVal / valence
+$06 / 4: dirDirID / folderID
+$0a / 4: dirCrDat / createDate
+$0e / 4: dirMdDat / modifyDate
+$12 / 4: dirBkDat / backupDate
+$16 /16: dirUsrInfo / userInfo
+$26 /16: dirFndrInfo / finderInfo
+$36 /16: dirResrv[4] / reserved[4]
```

CatDataFileRec / HFSCatalogFile (102 bytes)
```
+$00 / 2: cdrType+cdrResv2 / recordType
+$02 / 1: filFlags / flags
+$03 / 1: filType / fileType
+$04 /16: filUsrWds / userInfo
+$14 / 4: filFlNum / fileID
+$18 / 2: filStBlk / dataStartBlock
+$1a / 4: filLgLen / dataLogicalSize
+$1e / 4: filPyLen / dataPhysicalSize
+$22 / 2: filRStBlk / rsrcStartBlock
+$24 / 4: filRLgLen / rsrcLogicalSize
+$28 / 4: filRPyLen / rsrcPhysicalSize
+$2c / 4: filCrDat / createDate
+$30 / 4: filMdDat / modifyDate
+$34 / 4: filBkDat / backupDate
+$38 /16: filFndrInfo / finderInfo
+$48 / 2: filClpSize / clumpSize
+$4a /12: filExtRec / dataExtents
+$56 /12: filRExtRec / rsrcExtents
+$62 / 4: filResrv / reserved
```

CatDataThdRec / CatDataFThdRec / HFSCatalogThread (46 bytes)
```
+$00 / 2: cdrType+cdrResv2 / recordType
+$02 / 8: thdResrv[2] / reserved[2]
+$0a / 4: thdParID / parentID
+$0e /32: thdCName / nodeName[32]
```

DInfo / FndrDirInfo (16 bytes)
```
+$00 / 2: frRect.top
+$02 / 2: frRect.left
+$04 / 2: frRect.bottom
+$06 / 2: frRect.right
+$08 / 2: frFlags
+$0a / 2: frLocation.v
+$0c / 2: frLocation.h
+$0e / 2: frView / opaque
```

DXInfo / FndrOpaqueInfo (16 bytes)
```
+$00 / 2: frScroll.v
+$02 / 2: frScroll.h
+$04 / 4: frOpenChain
+$08 / 1: frScript
+$09 / 1: frXFlags
+$0a / 2: frComment
+$0c / 4: frPutAway
```

FInfo / FndrFileInfo (16 bytes)
```
+$00 / 4: fdType
+$04 / 4: fdCreator
+$08 / 2: fdFlags
+$0a / 2: fdLocation.v
+$0c / 2: fdLocation.h
+$0e / 2: fdFldr / opaque
```

FXInfo / FndrOpaqueInfo (16 bytes)
```
+$00 / 2: fdIconID
+$02 / 6: fdUnused[3]
+$08 / 1: fdScript
+$09 / 1: fdXFlags
+$0a / 2: fdComment
+$0c / 4: fdPutAway
```

## Bootable Volumes ##

The Macintosh looks for a specific structure in block 0 to decide whether a disk can be booted.
A number of system parameters are stored here, as well as executable code.  The format is
documented in IM:Files starting on page 2-57.

The block 0/1 layout is:

```
+$00 / 2: bbID - signature bytes, for HFS this must be $4c $4b ('LK')
+$02 / 4: bbEntry - entry point to boot code, expressed as a 68K BRA.S instruction
+$06 / 2: bbVersion - flag byte and boot block version number
+$08 / 2: bbPageFlags - "used internally"
+$0a /16: bbSysName - system filename, usually "System" (stored as string with leading length byte)
+$1a /16: bbShellName - Finder filename, usually "Finder"
+$2a /16: bbDbg1Name - first debugger filename, usually "MacsBug"
+$3a /16: bbDbg2Name - second debugger filename, usually "Disassembler"
+$4a /16: bbScreenName - file containing startup screen, usually "StartUpScreen"
+$5a /16: bbHelloName - name of startup program, usually "Finder"
+$6a /16: bbScrapName - name of system scrap file, usually "Clipboard"
+$7a / 2: bbCntFCBs - number of FCBs to allocate
+$7c / 2: bbCntEvts - number of event queue elements
+$7e / 4: bb128KSHeap - system heap size on 128K Mac
+$82 / 4: bb256KSHeap - "used internally"
+$86 / 4: bbSysHeapSize - system heap size on machines with >= 512K of RAM
+$8a / 2: filler - reserved
+$8c / 4: bbSysHeapExtra - minimum amount of additional System heap space required
+$90 / 4: bbSysHeapFract - fraction of RAM to make available for system heap
+$94 /nn: executable code, if any
```

The last two heap size fields are only present in "new"-style boot blocks.  A bit in the version
flags will tell you whether or not they are present.  These values override the earlier heap size
fields.

The `bbEntry` value is a 68K branch whose offset is relative to the start of the instruction + 2,
so `60 00 00 86` is a branch to 4 + $86 + 2 = $8c, appropriate for an "old"-style boot block.
A "new"-style boot block should be `60 00 00 8e`, branching to $94.

The `bbVersion` field is documented as a 16-bit value, but the first (high) byte holds flags,
while the second (low) byte holds the boot block version number.  The flags are:

bit | meaning
--- | -------
0-4 | reserved, must be 0
  5 | set if relative system heap sizing is to be used
  6 | set if the boot code in the boot blocks is to be executed
  7 | set if the new boot block header format is used

If bit 7 is clear (old format), then bits 5 and 6 are ignored, and the boot code is only executed
if the version is $0d.  If bit 7 is set (new format), then bit 6 determines whether or not the
code is executed.  IM:Files notes:

> Generally, however, the boot code stored on disk is ignored in favor of boot code stored in a
> resource in the System file.

Note: the System folder on a bootable Macintosh volume must be "blessed".  There are a few ways
to accomplish this; on Macintoshes with System 7, this can be done by booting a working system,
mounting the new volume, and then double-clicking on the System suitcase in the System folder.
The actual effect of this on the HFS filesystem is to record the CNID of the System folder in the
first entry of `drFndrInfo` in the MDB.  (There may be other actions, such as updating the EFI
data; see the Apple open-source [bless command](https://github.com/apple-opensource/bless).)

## Miscellaneous ##

HFS volumes can checked with the `fsck.hfs` command.  The x86-64 version found on Linux
crashes immediately (using hfsprogs 332.25-11build1 amd64), but the i386 version still works.
The command will fix minor problems without asking unless the "-n" flag is given.

The GS/OS System 6.0.1 FST has a serious bug that can cause file corruption.  A software patch is
available in the test file collection, in [`TestData/nufx/PatchHFS.shk`](../../TestData/nufx).
