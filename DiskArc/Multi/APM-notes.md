# Apple Partition Map (APM) #

## Primary References ##

- _Inside Macintosh: Devices_, chapter 3 (esp. 3-12+)
- https://en.wikipedia.org/wiki/Apple_Partition_Map

## General ##

Now called Apple Partition Map (APM), the partition scheme used on Macintosh computers didn't
initially have a fancy name, and was generally referred to as the "Macintosh partition format".
It was used on hard drives, CD-ROMs, and other large block devices to define multiple partitions.
The format was introduced with the Macintosh II, and was replaced by GUID Partition Table (GPT)
when the Macintosh line transitioned to x86 CPUs.

On an APM disk, block 0 holds the Driver Descriptor Record (DDR).  This defines the size of a
block and the number of blocks on the disk, but these fields are sometimes unreliable.  The block
size is de facto always 512 bytes, even on media like CD-ROMs that use 2048-byte blocks.

The partition map starts in block 1, and continues for a number of blocks specified by the first
entry.  Each block in the map holds the definition for one partition.  The location of a
partition is defined in "physical blocks", using the block size specified by the DDR.

The integers in Macintosh structures are usually interpeted as signed values.  Assuming 512-byte
blocks, a signed 32-bit block count allows disks of up to 1TB.  That doubles to 2TB if the count
is treated as unsigned.

## Structure ##

Block 0 holds the Driver Descriptor Record (DDR):
```
+$00 / 2: sbSig - device signature (big-endian 0x4552, 'ER')
+$02 / 2: sbBlkSize - block size of the device (usually 512)
+$04 / 4: sbBlkCount - number of blocks on the device
+$08 / 4: sbDevType - (reserved)
+$0a / 2: sbDevId - (reserved)
+$0c / 2: sbData - (reserved)
+$10 / 2: sbDrvrCount - number of driver descriptor entries
+$12 / 4: ddBlock - first driver's starting block
+$16 / 2: ddSize - size of the driver, in 512-byte blocks
+$18 / 2: ddType - operating system type (MacOS = 1)
+$1a /486: ddPad - ddBlock/ddSize/ddType entries for additional drivers (8 bytes each)
```

Some third-party utilities fill out the fields incorrectly.  For example, the C.V.Tech floptical
formatter sets both block size and count to zero, and CD-ROMs have been found with a block size
of 512 but a block count of zero.  It's best to determine the size of the device from the
device characteristics rather than the DDR.

The partition map begins in block 1.  Except for the DDR, every block on a disk must belong to
a partition (the map is self-referential).  Partitions must not overlap.

Each partition map entry block looks like this:
```
+$000 / 2: pmSig - partition signature (big-endian 0x504d, 'PM')
+$002 / 2: pmSigPad - (reserved)
+$004 / 4: pmMapBlkCnt - number of blocks in partition map
+$008 / 4: pmPyPartStart - block number of first block of partition
+$00c / 4: pmPartBlkCnt - number of blocks in partition
+$010 /32: pmPartName - partition name string (optional; some special values)
+$030 /32: pmParType - partition type string (names beginning with "Apple_" are reserved)
+$050 / 4: pmLgDataStart - first logical block of data area (for e.g. A/UX)
+$054 / 4: pmDataCnt - number of blocks in data area (for e.g. A/UX)
+$058 / 4: pmPartStatus - partition status information (used by A/UX)
+$05c / 4: pmLgBootStart - first logical block of boot code
+$060 / 4: pmBootSize - size of boot code, in bytes
+$064 / 4: pmBootAddr - boot code load address
+$068 / 4: pmBootAddr2 - (reserved)
+$06c / 4: pmBootEntry - boot code entry point
+$070 / 4: pmBootEntry2 - (reserved)
+$074 / 4: pmBootCksum - boot code checksum
+$078 /16: pmProcessor - processor type string ("68000", "68020", "68030", "68040")
+$088 /376: (reserved)
```

The first three fields (pmSig, pmSigPad, pmMapBlkCnt) must be the same in all entries.

The pmPartName and pmParType strings may be up to 32 bytes long.  The rest of the field should
be filled with zeroes.  (Note: a 32-byte string is not null-terminated.)  If the pmPartName begins
with "Maci" (as in "Macintosh"), the Macintosh Start Manager will perform checksum verification
of the device driver's boot code.

The documentation does not mention case-sensitivity, but examples exist with altered case.  It's
best to generate the strings exactly as shown, but perform case-insensitive comparisions when
looking for specific values.  The character set is also not documented, but it's reasonable
to assume that Mac OS Roman characters are allowed.

Each partition entry is stored in a separate block, and the blocks must be contiguous, so
expanding the partition map after it has been created is difficult.  Maps were often over-allocated
to allow room to expand.

The order in which partitions appear in the map is undefined.  If the disk is meant to be booted,
the HFS or ProDOS filesystem to boot should be the first entry in the map.

### Partition Type ###

The pmParType string specifies the contents of a partition.  Page 3-26 of
_Inside Macintosh - Devices_ lists partition type names defined by Apple:

 - "Apple_partition_map": the partition map itself
 - "Apple_Driver": device driver
 - "Apple_Driver43": SCSI Manager 4.3 device driver
 - "Apple_MFS": original Macintosh File System (64K ROM version)
 - "Apple_HFS": Hierarchical File System (128K and later ROM versions)
 - "Apple_Unix_SVR2": Unix file system
 - "Apple_PRODOS": ProDOS file system
 - "Apple_Free": unused
 - "Apple_Scratch": empty

A longer list can be found on [wikipedia](https://en.wikipedia.org/wiki/Apple_Partition_Map).

### Examples ###

The partitions found on a Mac Classic's 40MB hard drive were:

name        | type                 | processor | start | count
----------- | -------------------- | --------- | ----- | -----
"MacOS"     | Apple_HFS            |           |    96 | 80000
"Apple"     | Apple_partition_map  |           |     1 | 63
"Macintosh" | Apple_Driver         | 68000     |    64 | 32
"Extra"     | Apple_Free           |           | 80096 | 1996

The DDR for the drive had sbBlkSize=512, sbBlkCount=82092, ddType=1, ddSize=10, and ddBlock=64.
(Trivia: 80000 512-byte blocks is 40.96MB or 39.06MiB, so calling it "40MB" won't satisfy the
pedantic.)

CD-ROMs have a wider variety of configurations, though they're generally just the partition
map and some data partitions.  No driver partition or free space, and all fields other than the
block size and count are usually zero in the DDR.

In one case (the "Apple II GEM-CD"), the last partition had type="Apple_FREE" and was
significantly oversized.  Further, the DDR block count was zero.

In another case ("A2 ROMulan"), a partition called "Toast 5.1.3 HFS/Joliet Builder" started
at block 14465, leaving a very large gap between it and the partition map.  This violates the
requirement that all blocks be represented in the partition map.  (The CD-ROM image can be
opened directly in Windows despite being HFS formatted, suggesting that this is a "hybrid"
disc image.  The data in the gap is likely the ISO-9660 catalog.)

Some internal Apple CD-ROM images (e.g. "Wishing_Well_CD_Sum_91") essentially cut off in the
middle of the primary partition.  The Apple_HFS partition and HFS filesystem extend past the end
of the disk image file, and the Apple_Free partition starts completely off the end.

Bottom line: a fair amount of tolerance is required with this format.
