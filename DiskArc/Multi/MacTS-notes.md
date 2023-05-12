# Macintosh Plus SCSI Partition Map #

## Primary References ##

- _Inside Macintosh, Volume IV_ (1986), chapter 31 (esp. p.292)
  https://vintageapple.org/inside_o/pdf/Inside_Macintosh_Volume_IV_1986.pdf

## General ##

This is a very rare format, developed for use on SCSI hard drives attached to the Macintosh Plus.
It was used before APM, and only gets a passing notice in later documentation.  For example,
_Inside Macintosh: Devices_ notes on page 3-25, in the definition of the `pmSig` field:

> The partition signature. This field should contain the value of the pMapSIG constant ($504D).
> An earlier but still supported version uses the value $5453.

I refer to this as the "TS" partition format because of the signature word.

## Structure ##

Block zero holds a Driver Descriptor Record that is nearly identical to the
[APM definition](APM-notes.md), lacking only the `ddPad` definition (which defines drivers
beyond the first).

Block 1 holds the Device Partition Map:
```
$00 / 2: pdSig - signature (big-endian $5453, 'TS')
$02 / 4: pdStart - block number of first block of partition
$06 / 4: pdSize - number of blocks in partition
$0a / 4: pdFSID - file system ID ($54465331 'TFS1' for Macintosh Plus)
```

The `pdStart`/`pdSize`/`pdFSID` fields are repeated for successive partitions.  There is no count,
so the list is supposed to end when all three values are set to zero.

Looking at a very old CD-ROM image:

00000200: 5453 0000 0022 0004 a5ba 5446 5331 0000  TS..."....TFS1..
00000210: 0000 4456 6572 0003 0002 6420 696e 746f  ..DVer....d into
00000220: 2069 7473 656c 6620 6f72 2069 7473 206f   itself or its o
00000230: 776e 2066 6f6c 6465 722e 3654 6861 7420  wn folder.6That

The first partition is start=$00000022 size=$0004a5ba fsid=TFS1.  The second partition would
have a start block of zero, which apparently ends the list.  The rest of the data in the block
is garbage.
