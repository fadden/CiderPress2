# Disk Archive / Retrieval Tool (DART) Format #

## Primary References ##

 - DartInf.h header file - https://retrocomputing.stackexchange.com/a/31134/56
 - Release notes for v1.5.3 (Sep 1993): https://macgui.com/downloads/?file_id=23499

## General ##

The Disk Archive / Retrieval Tool (DART) was used by Apple to distribute disk images internally
and in some tech support products.  It was used contemporaneously with Disk Copy 4.2 (early 1990s),
and provided much the same functionality, but with the addition of data compression.  DART was
not an official product, and no specifications for the file format were published.

DART files have no official filename extension, though ".image" was sometimes added.  The ".dart"
filename extension maps to com.apple.disk-image-dart on modern macOS, but some information is
lost if the HFS file attributes aren't preserved.  The files were meant to be identified by the
creator 'DART', and the file type was used to clarify the disk's nature:

type | meaning
---- | -------
DMdf | old file type
DMd0 | DART preferences
DMd1 | Macintosh 400KB
DMd2 | Lisa 400KB
DMd3 | Macintosh 800KB
DMd4 | Apple II 800KB
DMd5 | MS-DOS 720KB
DMd6 | Macintosh 1440KB
DMd7 | MS-DOS 1440KB

DART computes the same checksum that Disk Copy 4.2 does, but instead of storing it in the file
header, it is placed in the resource fork of the DART image file.  The tag checksum is in
resource 'CKSM' with ID=1, and the data checksum is in 'CKSM' with ID=2.  The resource fork also
has a 'DART' ID=0 resource, which holds a string that indicates the DART application version and
shows the checksums in human-readable form.

## File Structure ##

Files have a header, followed by chunks of data, which may be stored with or without compression.
Each 20960-byte chunk holds 40 524-byte blocks, stored as 40 512-byte blocks followed by 40 sets
of 12-byte tag data.  An 800KB disk will have 40 chunks, while a 1440KB disk will have 72 chunks.
The chosen compression is applied individually to each chunk.

All multi-byte values are stored in big-endian order.

```
+$00 / 1: srcCmp - compression identifier (0=fast, 1=best, 2=not)
+$01 / 1: srcType - disk type identifier
+$02 / 2: srcSize - size of source disk, in kiB (e.g. 800 for an 800KB floppy)
+$04 /nn: bLength - array of 16-bit block lengths; nn is either 40*2 or 72*2
+$54 or +$94: compressed disk data
```

The disk type identifiers are:

 - 1: Macintosh (400KB/800KB)
 - 2: Lisa (400KB/800KB)
 - 3: Apple II (800KB)
 - 16: Macintosh 1440KB
 - 17: MS-DOS 720KB
 - 18: MS-DOS 1400KB

The `bLength` array will have 40 entries, for disks up to 800KB, or 72 entries, for 1440KB disks.
The meaning of each block length is somewhat variable:

 - For uncompressed data, the block length will be 20960 or -1 (0xffff).
 - For "fast" (RLE) compression, the length is in 16-bit words, i.e. half the length in bytes.
 - For "best" (LZH) compression, the length is in bytes.

Entries past the end of the disk, e.g. the last half of the entries for a 400KB image, will be
zero.  Individual chunks that fail to compress will be noted with a block length of 0xffff and
stored without compression.

## Compression ##

Compression is always applied by DART v1.5.  It's unclear what generated uncompressed files,
though it might be an earlier version of the application.

The "fast" compression algorithm uses a word-oriented run-length encoding algorithm.  The data is
treated as a series of 16-bit big-endian integers.  The first value is a signed count of 16-bit
words.  If it's positive, the next N words should be copied directly to the output.  If it's
negative, the following word is a pattern, and -N copies of the pattern should be written to the
output.

For example, the sequence `fe00 0000 0003 4244 df19 1e19` generates 1024 zero bytes (0xfe00 == 512),
followed by the values `42 44 df 19 1e 19`.

"Best" compression uses a slightly modified Yoshizaki/Okumura LZHUF algorithm.  The code was
changed to omit the leading length word, and to initialize the match window with 0x00 bytes
instead of 0x20.

Uncompressible chunks are rare, because each chunk includes 480 bytes of tag data, which is
zero-filled on HFS disks.
