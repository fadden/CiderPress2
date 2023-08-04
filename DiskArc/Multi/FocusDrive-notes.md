# FocusDrive Partition Format #

## Primary Sources ##

- Reverse engineering by Ranger Harke

## General ##

The FocusDrive is an IDE disk controller for the Apple II, sold in the early 2000s.  It was
designed by Parsons Engineering.

## Layout ##

Block 0 has an identifier string and the partition table.  Blocks 1 and 2 hold partition names.
Up to 30 partitions can be defined.

The partition map layout is:
```
+$00 /14: signature, "Parsons Engin." in ASCII
+$0e / 1: (unknown, always zero?)
+$0f / 1: number of partitions (1-30)
+$10 /16: (unknown; serial number?)
+$20 /nn: array of partition entries:
  +$00 / 4: start LBA
  +$04 / 4: size, in blocks
  +$08 / 4: (unknown, always zero?)
  +$0c / 4: (unknown)
```
All values are little-endian.

Blocks 1 and 2 hold the partition names.  Each name is 32 bytes of ASCII, padded with zeroes.

The area where you'd expect to find the first entry is actually used to hold the number of blocks
not included in the partition map; the 32-bit value appears at +$04.  The name of the first
partition is found at +$20.  The last entry, in block 2 at +$1e0, is always blank.
