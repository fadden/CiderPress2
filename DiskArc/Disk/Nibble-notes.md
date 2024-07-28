# Nibble Image Files #

## Primary References ##

- _Beneath Apple DOS_, chapter 3; the 5th printing has important corrections:
  https://archive.org/details/Beneath_Apple_DOS_alt/page/n6/mode/1up
- _Understanding the Apple IIe_, chapter 9:
  https://archive.org/details/Understanding_the_Apple_IIe/page/n257/mode/2up
- https://retrocomputing.stackexchange.com/a/664/56 (max nibbles per 5.25" track)
- NEC uPD72070 Floppy Disk Controller datasheet (section 1.3.1)
- MAME 3.5" disk driver: https://github.com/mamedev/mame/blob/master/src/lib/formats/ap_dsk35.cpp
- various from: http://bitsavers.informatik.uni-stuttgart.de/pdf/apple/disk/sony/, notably
  "Macintosh Versus IIgs Sector Sizes and Two-to-One Interleave" (2-May-1988 memo) and
  sheets 33-36 of 699-0285-A "Specification for 3.5 Inch Single Sided Disk Drive" (which
  describes the sector format in detail)
- _Lisa Hardware Manual_, chapter 6

## GCR Encoding ##

The Apple II receives a stream of bits from the floppy disk controller.  For a variety of reasons
(see _Beneath Apple DOS_ for an introduction, or _Understanding the Apple IIe_ chapter 9 for a
deep dive), it's not possible to write arbitrary values to the stream.  The original Disk ][
drive controller imposed a requirement that the bit stream not have more than one consecutive
zero bit, so 1011 was okay, but 1001 was not.  An update to the controller allowed two consecutive
zero bits.  Every byte written to the stream also had to have a '1' in the high bit, to make it
easy to see when 8 bits had been read.

There are 70 valid octets that have a '1' in the high bit and no more than two consecutive '0'
bits.  If we add an additional requirement that there must be two adjacent 1 bits, excluding bit 7,
we get exactly 64 possibilities.  We can use a table of these values to encode three bytes in four
octets: three six-bit values from the original bytes, plus one six-bit value that has the remaining
two bits of each byte.  This is called "6&2" encoding, and is used on 16-sector 5.25" disks and
on 3.5" disks.

A similar approach for an encoding that does not allow any consecutive '0' bits yields 32 values,
allowing five bytes to be stored as eight octets.  This is "5&3" encoding, used on 13-sector 5.25"
floppy disks.

To align the software with the bytes in the bit stream, "self-sync" bytes are written
occasionally, usually between parts of sectors.  These bytes are longer than normal, 9 or 10 bits.
(See _Beneath Apple DOS_ for an illustration.)  They're written by delaying the delivery of the
next byte by one or two bit periods, effectively appending one or two zero bits to the end of
the byte.

This type of encoding is called Group Code Recording (GCR).  Some other contemporary drives used
Modified Frequency Modulation (MFM) encoding, which places a '1' bit between every data bit.
On the Apple II this approach is used in some of the address header fields on 5.25" disks, and
is referred to as "4&4" encoding.

### Storage ###

There are two basic approaches for capturing "raw" data from a floppy into a disk image file:
  1. Record all bits.  This correctly captures regular data and self-sync bytes, but is
     difficult to do accurately with unmodified floppy drive hardware.  WOZ and FDI do this.
  2. Record full bytes only.  This is easy to do with standard drive hardware, but loses the
     self-sync byte information.  NIB and APP (Trackstar) do this.

Byte-oriented is easier to capture, bit-oriented is more accurate.  For disks with a standard
sector format, the choice is largely irrelevant, but some copy-protection schemes relied on
detecting unusual patterns of bits.  (See e.g. https://retrocomputing.stackexchange.com/q/37/56 .)

It's possible to make a lower-level recording by measuring the time between magnetic flux
transitions.  This requires modified drive hardware, and is more difficult to work with.

## 5.25" Floppy Disks ##

Apple II disks are single-sided with 35 tracks, though some drives allowed up to 40 tracks.  The
drives have a constant angular velocity, so every track stores the same amount of data: 6400 bytes
at 300rpm.  Minor changes in drive speed will increase or decrease this value slightly.

Track 0 is at the outside of the disk.

On a 13-sector disk, each sector looks like this:
```
+$000 / 3: address prolog ($d5 $aa $b5)
+$003 / 2: 4&4enc disk volume number
+$005 / 2: 4&4enc track number
+$007 / 2: 4&4enc sector number
+$009 / 2: 4&4enc address prolog checksum
+$00b / 3: address epilog ($de $aa $eb)
+$00e /10: 10 self-sync 9-bit $ff bytes
+$018 / 3: data prolog ($d5 $aa $ad)
+$01b /410: 5&3 enc nibblized sector data (256 -> 409.67 bytes, rounded up to 410)
+$1b5 / 1: checksum
+$1b6 / 3: data epilog ($de $aa $eb)
+$1b9 /nn: ~40 self-sync 9-bit $ff bytes between sectors
```
Given approximately 481 bytes/sector, 6400/481 = 13.3 sectors/track.

The DOS 3.2 formatter only writes address fields.  The data prolog and epilog aren't written
until the first time a sector is written, so attempting to read new sectors on a 13-sector disk
will fail.

On a 16-sector disk, each sector looks like this:
```
+$000 / 3: address prolog ($d5 $aa $96)
+$003 / 2: 4&4enc disk volume number
+$005 / 2: 4&4enc track number
+$007 / 2: 4&4enc sector number
+$009 / 2: 4&4enc address prolog checksum
+$00b / 3: address epilog ($de $aa $eb)
+$00e / 5: 5 self-sync 10-bit $ff bytes (~6.2 octets) following 2-3 bytes of garbage
+$013 / 3: data prolog ($d5 $aa $ad)
+$016 /342: 6&2 enc nibblized sector data (256 -> 341.33 bytes, rounded up to 342)
+$16c / 1: checksum
+$16d / 3: data epilog ($de $aa $eb)
+$170 /nn: ~20 self-sync 10-bit $ff bytes between sectors (~50 octets)
```
Given approximately 400 bytes/sector, 6400/400 = 16 sectors/track.

The DOS 3.3 formatter wants to distribute the sectors evenly across the track, but is not able
to know the rotational speed of the drive ahead of time. It begins by writing 128 self-sync bytes
to try to avoid having an unwritten gap between sectors 15 and 0, and sets the initial number of
sync bytes between sectors to 40.  This is likely too large to fit on the track, so the formatter
repeatedly writes and verifies the track,  decrementing the inter-sector sync byte count each
time.  When the full track has been successfully written, the formatter moves on to the next
track, without resetting the count.  Since the drive uses a constant angular velocity, the same
spacing should be usable on every track, though the sync count can be reduced further if necessary.

DOS tests only the first two epilog bytes ($de $aa).  It's not uncommon for the $eb in the
address epilog to get stomped on.

The drive head is moved by turning magnets on and off.  Each full track requires toggling two
magnets.  Toggling only one will position the drive head on a "half track".  For most drives the
magnetic head is too wide for this to be reliable, as writing to one track may disturb the data
on nearby half tracks, so this was only used for copy protection.  Toggling two adjacent magnets
on with proper timing could position the head even more finely, allowing "quarter tracks".

## 400KB/800KB 3.5" Floppy Disks ##

The 3.5" drives sold by Apple for the Lisa, Macintosh, and Apple II used a ZCAV (Zoned Constant
Angular Velocity) approach, which changes the speed at which the drive rotates to allow more
data to be stored on the longer tracks at the outside of the disk.  This allows a greater storage
capacity than CAV (Constant Angular Velocity) drives that spin at a fixed speed, without requiring
changes to the timing at which bits are read and written.  This is a form of ZBR
([Zone Bit Recording](https://en.wikipedia.org/wiki/Zone_bit_recording)).

In practical terms, we start with 12 sectors per track at the outside of the disk, and subtract
one sector for every 16 tracks closer to the center.  This forms 5 speed zones:

 - track 0-15 : 12 sectors
 - track 16-31: 11 sectors
 - track 32-47: 10 sectors
 - track 48-63: 9 sectors
 - track 64-79: 8 sectors

(Math: 12\*16 + 11\*16 + 10\*16 + 9\*16 + 8\*16 = 800 sectors per side.)

Each sector holds 524 bytes of data.  They look like this:
```
+$000 /36: self-sync pattern: 36 10-bit $ff bytes (spans 45 octets in bit stream)
+$024 / 3: address prolog ($d5 $aa $96)
+$027 / 1: 6&2enc low part of track number: 0-79 mod 63
+$028 / 1: 6&2enc sector number (0-11)
+$029 / 1: 6&2enc side number ($00 or $20) and high part of track number ($01 for tracks >= 64)
+$02a / 1: 6&2enc format ($22 or $24)
+$02b / 1: 6&2enc address checksum: (track ^ sector ^ side ^ format) & $3f
+$02c / 2: address epilog ($de $aa)
+$02e / 1: pad byte ($ff), "where the write electronics were turned off"
+$02f / 5: self-sync pattern: 5 10-bit $ff bytes (spans 6.25 octets in bit stream)
+$034 / 3: data prolog ($d5 $aa $ad)
+$037 / 1: duplicate 6&2enc copy of sector number (0-11)
+$038 /699: 6&2enc nibblized sector data (524 -> 698.67 bytes, rounded up to 699)
+$2f3 / 4: 6&2enc 24-bit checksum
+$2f7 / 2: data epilog ($de $aa)
+$2f9 / 1: pad byte ($ff), "where the write electronics were turned off"
```
Because of the self-sync bytes, the 762-byte sector actually spans 772.25 octets.  (The
699-0285-A doc, on page 34, says the minimum size is "733.5 code bytes", but that's because
it only shows the minimum 5 self-sync bytes at the start.)

The Mac-vs-IIgs memo says the Apple IIgs formatter has a lower FCLK frequency, and compensates
for the shorter bits by writing 30 extra self-sync "nybles" between sectors.  The document
lists the "sector size" as increasing from 762 to 792, and explains how this results in a similar
sector spacing.

A track dump of an Apple IIgs disk showed ~51 octets between sectors (~41 sync bytes), though
it's not clear what hardware was used to format the disk.  The 699-0258-A document mandates 5
self-sync bytes at a minimum, though strictly speaking only 4 are required to achieve bit
synchronization.

The first 12 bytes in the 524-byte sector are "tag" bytes, which were used by the Lisa OS
to hold filesystem structures, and later used by the Macintosh MFS filesystem to hold redundant
data that could be used by disk recovery applications.  These bytes are also available to HFS,
but they don't appear to be used there, presumably because HFS was used on a wider range of disk
devices and couldn't rely on the presence of the tags.

The 24-bit checksum on the data area is calculated with a fairly complicated algorithm.  The
AppleDisk 3.5" driver for GS/OS has a text string indicating that the software is protected
under US Patent 4,564,941, "Error detection system" (filed 1983, granted 1986).  The patent
describes, among other things, an "interleaved" checksum algorithm that may be what was used for
the checksum here.

### Disk Interleave ###

Sectors on 3.5" disks are physically interleaved by the disk initializer.  The layout is
complicated slightly by the fact that the number of sectors varies as you move across the disk.

For a 2:1 interleave (from 699-0285-A):
  - group 1: 0-6-1-7-2-8-3-9-4-10-5-11
  - group 2: 0-6-1-7-2-8-3-9-4-10-5
  - group 3: 0-5-1-6-2-7-3-8-4-9
  - group 4: 0-5-1-6-2-7-3-8-4
  - group 5: 0-4-1-5-2-6-3-7

For a 4:1 interleave (observed):
  - group 1: 0-3-6-9-1-4-7-10-2-5-8-11
  - group 2: 0-3-6-9-1-4-7-10-2-5-8
  - group 3: 0-5-3-8-1-6-4-9-2-7
  - group 4: 0-7-5-3-1-8-6-4-2
  - group 5: 0-2-4-6-1-3-5-7

Generating these algorithmically is straightforward:

    int entry = 0
    for (int i = 0 to number_of_sectors) {
        if (table[entry] is already set) {
            entry = (entry + 1) % number_of_sectors
        }
        table[entry] = i
        entry = (entry + interleave) % number_of_sectors
    }

### Format Byte ###

The value of the "format" byte in the address header of every sector is defined somewhat
vaguely by sheet 34 of the 699-0285-A "Specification for 3.5 Inch Single Sided Disk Drive"
document:
```
Format - encoded format specification:
      decoded bit 5 = 0 for single-sided formats
      decoded bits 0-4 define the format interleave:
      standard 2:1 interleave formats have a 2 in this field
```

The documentation for an old Mac OS floppy disk device driver (found in a doc for the LPX-40
logic board) notes these values:
  - 0x02: 720K, 1440K, and 2880K MFM
  - 0x12: Macintosh 400K GCR
  - 0x22: Macintosh 800K GCR
  - 0x24: Apple II 800K GCR

800KB disks are usually formatted with a 2:1 interleave on the Macintosh, but the Apple IIgs can
format them for 4:1.

The format byte is also stored in the "formatByte" field of DiskCopy 4.2 images; according to the
file type note documentation, "DiskCopy uses [0x22] for all Apple II disks not 800K in size, and
even for some of those".  It appears that the "diskFormat" and "formatByte" fields are expected
to be received from and passed into a DiskCopy control function in the floppy driver without
further interpretation.

https://www.discferret.com/wiki/Apple_DiskCopy_4.2 claims that Mac 400K should be 0x02, and
0x12 is actually Lisa 400K.  720KB/1440KB MFM disks are double-sided, and so use 0x22.

The crux of the issue is the meaning of bit 4, which by the Apple definitions is zero for all
disks except Mac 400K.  It would be more consistent to use the discferret definition, which
uses the bit as a "Lisa" flag.

The MAME ap_dsk35.cpp source code has a completely different take:
  - 0x00: Apple II
  - 0x01: Lisa
  - 0x02: Mac MFS (single sided)?
  - 0x22: Mac MFS (double sided)?

It's unclear whether anything actually relies on the value as anything but a hint.  The number
of sides physically present is not determined by the address field of a given disk sector, and
the sector interleave could be determined by just looking at the order in which sectors appear.

## 5.25" "Twiggy" Disks ##

The first Apple Lisa computers came with [FileWare](https://en.wikipedia.org/wiki/Apple_FileWare)
floppy disk drives, commonly referred to as "Twiggy" drives.  These used special 5.25" disks that
had two windows instead of one.  The drives were intended to be made available for the Apple II
and ///, but were never shipped.  Later versions of the Lisa used 3.5" disks.

The disks were encoded in GCR, using ZCAV (Zoned Constant Angular Velocity) to get additional
sectors near the outside of the disk.  Disks were double-sided and had 46 tracks.  The speed
changed every few tracks:

Tracks | Sectors | RPM
------ | ------- | -----
 0-3   | 22      | 218.3
 4-10  | 21      | 228.7
 11-16 | 20      | 240.1
 17-22 | 19      | 252.7
 23-28 | 18      | 266.8
 29-34 | 17      | 282.5
 35-41 | 16      | 300.1
 42-45 | 15      | 320.1

(Math: 4\*22 + 7\*21 + 6\*20 + 6\*19 + 6\*18 + 6\*17 + 7\*16 + 4\*15 = 851 sectors per side.)

Each sector holds 524 bytes of data.  The first 12 bytes are the "tag" bytes that are handled
by the operating system, the rest are standard sector data.  Sectors are encoded:
```
+$000 /32: self-sync pattern: 32 10-bit $ff bytes (spans 45 octets in bit stream)
+$020 /10: (only before sector 0) 10 $a9 bytes for speed synchronization
+$020 / 3: address prolog ($d5 $aa $96)
+$023 / 1: 6&2enc track number (0-45)
+$024 / 1: 6&2enc sector number (0-21)
+$025 / 1: 6&2enc side number ($00 or $01)
+$026 / 1: 6&2enc volume ($00=Apple II or ///, $01=Lisa, $02=Mac)
+$027 / 1: 6&2enc address checksum: (track ^ sector ^ side ^ format) & $3f
+$028 / 2: address epilog ($de $aa)
+$02a / 1: pad byte ($ff), "where head is turned off"
+$02b / 5: self-sync pattern: 5 10-bit $ff bytes (spans 6.25 octets in bit stream)
+$030 / 3: data prolog ($d5 $aa $ad)
+$033 / 1: duplicate 6&2enc copy of sector number (0-21)
+$034 /699: 6&2enc nibblized sector data (524 -> 698.67 bytes, rounded up to 699)
+$2ef / 3: 24-bit checksum
+$2f2 / 2: data epilog ($de $aa)
+$2f4 / 1: pad byte ($ff), "where head is turned off"
```

Total disk storage capacity was around 871 kB.

## Miscellaneous ##

A "nibble" is a 4-bit value.  The original Apple II floppy format used FM-style 4&4 encoding to
store sector data (allowing 10 sectors per track), so each byte written held one nibble.  When
this was upgraded to 5&3 the terminology stuck, in much the same way that 10-bit sync patterns
are still referred to as "bytes".

Some sources spell nibble with a 'y' ("nybble").  Apparently this was popularized on the Apple II
by Wozniak; see e.g. https://www.folklore.org/StoryView.py?project=Macintosh&story=Nybbles.txt .
The "official" spelling uses an 'i' as in "bit".  "byte" was spelled differently because "bit" and
"bite" are too similar.
