# MacBinary File Format #

## Primary Sources ##

 - "Macintosh Binary Transfer Format ("MacBinary") Standard Proposal", revision 3, dated 6-May-1985
 - "MacBinary II Standard", revised 24-Jul-1987
 - "MacBinary III Standard", revised Dec-1996
 - https://code.google.com/archive/p/theunarchiver/wikis/MacBinarySpecs.wiki

## General ##

[MacBinary](https://en.wikipedia.org/wiki/MacBinary) was introduced as a way to store and transmit
the data and resource forks of a Macintosh file as a single unit, along with file metadata such
as the type, creator, and timestamps.  It could be added and stripped automatically by serial file
transfer programs, and is commonly found on older Macintosh file uploads found on the Internet.

Like other 1980s-era transmission formats, the format assumes transmission in 128-byte chunks
(e.g. XMODEM protocol).  To simplify the encoding and decoding software, the file is padded to
conform to this packet size.

The MacBinary standard is a fairly straightforward encoding of the MFS/HFS file data.
MacBinary II is a backward-compatible extension that added a few additional fields and a CRC
on the header contents.  MacBinary III added a couple of miscellaneous fields and a signature.
Of these, MacBinary II appears to be the most common.

MacBinary files conventionally have the file extension ".bin" or ".macbin".

Distinguishing a MacBinary file from any other binary blob is difficult, because most of the
fields are or can be zero.  This is probably because the authors intended it for use as a
transmission wrapper rather than an archival storage format.  MacBinary II added a 16-bit CRC
on the header, and MacBinary III added a signature word, both of which make positive identification
easier and more reliable.

## File Structure ##

MacBinary files have three parts: the 128-byte header, followed by the data fork, then the
resource fork.  The file fork data is padded with zeroes to ensure it ends on a 128-byte boundary.
An "info" section with the Finder "Get Info" FCMT data follows, though the documentation disputes
whether this actually happened before the MacBinary III era.  (Presumably this should also be
padded to a 128-byte boundary, but the specification doesn't mention this.)

Files may or may not actually end on a 128-byte boundary.  Some MacBinary creators apparently
didn't pad the last stretch of file.

All multi-byte integers are in big-endian order.  Fields identified with "(2)" or "(3)" were
added in MacBinary II or III, and must be zero in earlier versions of the format.  (In practice,
some implementations left garbage in the unused fields.)

The file header is:
```
+$00 / 1: version byte, must be zero
+$01 /64: filename, with leading length byte
+$41 /16: Finder FInfo block (originated in MFS)
  +$00 / 4: fdType - file type
  +$04 / 4: fdCreator - creator
  +$08 / 2: fdFlags - flags
  +$0a / 2: fdLocationV - file's vertical position within window
  +$0c / 2: fdLocationH - file's horizontal position within window
  +$0e / 2: fdFldr - ID of directory that contains file
+$51 / 1: "protected" flag (0 or 1)
+$52 / 1: reserved, must be zero
+$53 / 4: data fork length, in bytes
+$57 / 4: resource fork length, in bytes
+$5b / 4: creation date/time
+$5f / 4: modification date/time
+$63 / 2: (2) length of Get Info comment
+$65 / 1: (2) low byte of Finder flags (bits 8-15 are in high byte of FInfo fdFlags field)
+$66 / 4: (3) signature 'mBIN'
+$6a / 1: (3) script of filename (fdScript field from FXInfo)
+$6b / 1: (3) extended Finder flags (fdXFlags field from FXInfo)
+$6c / 8: reserved, must be zero
+$74 / 4: (2) total length of files when unpacked; only meaningful with compression (never used?)
+$78 / 2: (2) length of a secondary header that immediately follows this one
+$7a / 1: (2) version number of MacBinary II that uploading program is written for ($81/$82)
+$7b / 1: (2) minimum MacBinary II version needed to read this file ($81)
+$7c / 2: (2) CRC of previous 124 bytes
+$7e / 2: computer/OS ID bytes, must be zero; field was dropped in (2)
```

If the secondary header is defined, skip that many bytes after the file header is read, rounded
up to the next 128-byte boundary.  (This has reportedly never been used, outside of a few
experiments.)

Filenames use the Mac OS character set.  Mac OS Roman may be assumed.  The filename field
holds the filename only, not a partial path, so the maximum length is 31 characters.  The
original MacBinary standard didn't note this, MacBinary II claimed 1-63 characters, but
MacBinary III reduced it to 1-31.  The filename must not include colons (':').

The CRC is a CRC-16/XMODEM, though that isn't explicitly mentioned in the specification.

For the version fields, MacBinary II is version 129, MacBinary III is version 130.

MacBinary only stored the high byte of the Finder FInfo flags.  According to the specification,
the low byte of the flags field must be zero.  The contents of the low byte were later added to
the MacBinary II header, at +$65.

The flags values are:
```
  $01 - bundle resource has been initialized
  $02 - (reserved, must be zero)
  $04 - has custom icon
  $08 - is stationery pad
  $10 - name and icon are locked
  $20 - has bundle
  $40 - invisible
  $80 - is alias
```

Timestamps are unsigned 32-bit values indicating the time in seconds since midnight on
Jan 1, 1904, in local time.  (This is identical to MFS / HFS.)
