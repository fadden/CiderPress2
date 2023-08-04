# MicroDrive Partition Format #

## Primary Sources ##

- Reverse engineering + notes from Joachim Lange
- https://wiki.reactivemicro.com/MicroDrive/Turbo

## General ##

The MicroDrive Turbo is an Apple II IDE controller with DMA support.  The card allows IDE hard
drives to be attached, and includes a Compact Flash card socket.  The card was designed by
Joachim Lange of ///SHH Systeme and first released in 1996.

## Layout ##

Block 0 holds the partition map, which defines up to 16 partitions.  The first partition starts
at block 256.

The partition map is defined:
```
+$00 / 2: magic (0xccca, 'JL' in little-endian high ASCII)
+$02 / 2: number of cylinders
+$04 / 2: (reserved)
+$06 / 2: number of heads per cylinder
+$08 / 2: number of sectors per track
+$0a / 2: (reserved)
+$0c / 1: number of partitions in first chunk (0-7)
+$0d / 1: number of partitions in second chunk (0-7)
+$0e /10: (reserved)
+$18 / 2: romVersion for IIgs; indicates ROM01 or ROM03
+$1a / 6: (reserved)
+$20 / 4: start LBA of partition #0
  ...
+$3c / 4: start LBA of partition #7
+$40 / 4: size, in blocks, of partition #0
 ...
+$5c / 4: size, in blocks, of partition #7
+$60 /32: (reserved)
+$80 / 4: start LBA of partition #8
  ...
+$9c / 4: start LBA of partition #15
+$a0 / 4: size, in blocks, of partition #8
 ...
+$bc / 4: size, in blocks, of partition #15
+$c0 /nn: (reserved)
```
All values are little-endian.

The partition size values are in the low 3 bytes of the size fields.  The high byte is used "for
switching drives in a two-drive configuration", so the size values should always be masked
with 0x00ffffff.
