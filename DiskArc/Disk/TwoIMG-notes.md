# Apple II Universal Disk Image (2IMG) #

Primary references:
 - https://web.archive.org/web/19981206023530/http://www.magnet.ch/emutech/Tech/
 - https://groups.google.com/d/msg/comp.emulators.apple2/xhKfAlrMBVU/EkJNLOCweeQJ

## General ##

The format was developed by Apple II emulator authors to address a couple of problems:

 1. Disk image file order ambiguity.  Unadorned disk images can be DOS-order or ProDOS-order,
    but were often simply named ".dsk".  This led to emulators being unable to open disk images
    unless they were somehow able to auto-detect the layout.
 2. DOS volume numbers.  The DOS volume number is stored in the disk VTOC, but is also stored in
    the address field of every disk sector.  When a disk image is stored as sectors rather than
    nibbles, the sector volume number is lost.  This caused problems with a handful of disks
    that required the correct value.  Technically this isn't a "DOS volume number" since it's
    found on all 5.25" disks, but only DOS cares.
 3. Lack of metadata.  Comments are nice.

The format was (unofficially) assigned the ProDOS file type $e0 / $0130.  Files may use the
extension ".2mg" or ".2img".

## File Structure ##

Files have four parts: header, data, comment, and "creator data".  The chunks must appear
in that order.

The header is:
```
+$00 / 4: magic file signature, a 4-char string "2IMG" ($32 $49 $4d $47)
+$04 / 4: creator signature code, a 4-char string
+$08 / 2: header length, in bytes
+$0a / 2: file format version (always 1)
+$0c / 4: image data format (0=DOS order, 1=ProDOS order, 2=nibbles)
+$10 / 4: flags and DOS 3.3 volume number
+$14 / 4: number of 512-byte blocks; only meaningful when format==1
+$18 / 4: offset from start of file to data (should be 64, same as header length)
+$1c / 4: length of data, in bytes
+$20 / 4: offset from start of file to comment
+$24 / 4: length of comment, in bytes
+$28 / 4: offset from start of file to creator data
+$2c / 4: length of creator data, in bytes
+$30 /16: reserved, must be zero (pads header to 64 bytes)
```

All values are in little-endian order.  The document does not specify signed vs. unsigned, but
given the target platform limitations it's reasonable to treat the values as signed.

The meaning of the "header length" field is a little confusing: the magnet.ch document says,
"the length of this header which equals 52 bytes as of this writing".  The header shown in that
document is 48 bytes without the padding at the end, or 64 bytes with.  (Guess: the header length
and file format fields were originally 4 bytes, but they were downsized and the padding was added,
and the author neglected to update the documentation.)  In practice, the header length is 64.

For an image with format 1 (ProDOS), the data length will be equal to the number of 512-byte
blocks * 512.  (The block count field seems redundant.  However, some images created by `WOOF`
have a meaningful block count but a zero data length.)

The "flags" word has multiple fields:

Bit  | Description
---- | -----------
 0-7 | sector volume number (0-254), if bit 8 is set; otherwise zero
   8 | if set, sector volume number is in low bits
9-30 | reserved
  31 | if set, disk is write-protected

If the sector volume number is not specified explicitly, 254 should be assumed.

The data chunk holds the raw sector or nibble data.  The position and length are fixed.

The comment chunk is plain ASCII text.  The end-of-line terminator is not specified in the
documentation, but CR is reasonable to assume.  (The original CiderPress uses and expects CRLF in
its 2IMG properties tool.  This seems incorrect given the format's origins.  Applications should
convert CRLF to CR.)

The creator data section can hold anything at all; the creator signature field allows
applications to recognize the data.  If the creator signature is changed, the creator data
should be discarded.

If the comment or creator chunks are not included, the relevant offset and length fields are
set to zero.

### DOS / ProDOS Format ###

These behave the way they do for an unadorned sector format.  Format 0 was intended for
DOS-order 5.25" floppies, while format 1 was intended for any block-ordered disk image.

There is one aberration: the original CiderPress would incorrectly allow non-5.25" disk images
to be stored in DOS order.  For example, an 800K ProDOS disk could be stored that way.  It
essentially treats the image as a 16-sector disk with 200 tracks.  This does not correspond to
any real-life media, and is generally not useful.  However, such disks may exist.

### Nibble Format ###

The magnet.ch site, archived Dec 1998, says:

> .NIB images are made up of 35 tracks. Each track has $1A00 (6656) bytes. There's no header
> structure. The first disk byte of track 0 starts at file offset +0, the first byte of track 1 at
> file offset +$1A00, and so on.

This makes the content of the 2IMG nibble format equivalent to ".nib" files.  This offers limited
benefits over the .nib format, as the file order of a .nib file is not ambiguous, and the DOS
volume number is captured directly.  Nibble-format 2IMG files are rare.

### Creator Codes ###

Known creator codes (mostly old emulators):

Code   | Application
------ | -----------
`!nfc` | ASIMOV2
`APSX` | ?
`B2TR` | Bernie ][ the Rescue
`CTKG` | Catakig
`CdrP` | CiderPress (original)
`CPII` | CiderPress II
`SHEP` | ?
`ShIm` | Sheppy's ImageMaker
`WOOF` | Sweet 16
`XGS!` | XGS
