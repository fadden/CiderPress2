# NuFX (ShrinkIt) Compression Format #

## Primary References ##

- Apple II File Type Note $e0/8002
- _Hacking Data Compression_, Lesson 9 (https://fadden.com/apple2/hdc/lesson09.html)
- NufxLib source code

## General ##

The data compression performed by ShrinkIt and GS/ShrinkIt uses a combination of run-length
encoding (RLE) and Lempel-Ziv-Welch sequence encoding (LZW; see
https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Welch).  The code was developed by
Kent Dickey and Andy Nicholas.

The original goal was to compress 5.25" disk images, offering a significant improvement over
programs like Dalton's Disk Disintegrator (DDD), which combined RLE with a simplified Huffman
encoding.  Each track on a 5.25" floppy holds 4KB, so compressing each track as an individual
entity is a natural fit.  ShrinkIt evolved into a more general-purpose file archiver, but some
of the original disk-oriented aspects remained.

For the Apple IIgs version of ShrinkIt, an improved version of the LZW algorithm was used.
In the original algorithm (now dubbed LZW/1), the table of sequences learned by the compressor
is reset for every 4KB chunk.  In LZW/2, the table is only reset when the table runs out of
space, or when LZW fails to make a 4KB chunk smaller.

## Detail ##

The run-length encoder will transform any run of more than three bytes to:

  <delim> <count-1> <value>

The delimiter is specified in the compressed data, but ShrinkIt always uses $db.

The length of a run is encoded as (count - 1), allowing runs up to 256 bytes.  Delimiters
are escaped by encoding them as a run, no matter how many appear.  The worse case scenario
is the delimiter alternating with non-delimiter values, resulting in a 2x expansion.

If RLE fails to make the contents of the 4KB buffer smaller, the uncompressed data is used.

If the file isn't an exact multiple of 4096 bytes, the extra space at the end of the buffer
is filled with zeroes.  These zeroes are included in the compressed output, and for LZW/1 they
are included in the CRC calculation as well.

The LZW pass takes the output from the RLE pass and compresses it.  The output uses
variable-width codes from 9 to 12 bits wide, inclusive.  Code 0x0100 is reserved for table
clears, so the first code used for data is 0x0101.

If LZW fails to make the contents smaller, the output of the RLE pass is used instead.

The output of the LZW/1 compressor includes a CRC at the *start* of the data, which makes it
unsuited for streaming.  This was removed from LZW/2 (GS/ShrinkIt stores a CRC in the thread
header instead).

## File Layout ##

The compressed data starts with a short header, followed by a series of compressed data chunks.
The layout is slightly different for LZW/1 and LZW/2.  All multi-byte values are little-endian.

LZW/1 header:
```
+$00 / 2: CRC-16/XMODEM on the uncompressed data
+$02 / 1: low-level volume number for 5.25" disks
+$03 / 1: delimiter value for run-length encoding (usually 0xdb)
```

LZW/1 chunk:
```
+$00 / 2: length after RLE compression; will be 4096 if compression failed
+$02 / 1: LZW flag (0 or 1)
+$03 /nn: (compressed data)
```

LZW/2 header:
```
+$00 / 1: low-level volume number for 5.25" disks
+$01 / 1: delimiter value for run-length encoding (usually 0xdb)
```

LZW/2 chunk:
```
+$00 / 2: bits [0,12]: length after RLE compression; bit [15]: LZW flag
[if LZW succeeded]
 +$02 / 2: length of compressed chunk, including the 4 header bytes
+$xx /nn: (compressed data)
```

## Notes ##

The disk volume number stored in the header is only useful for 5.25" floppy disk images, and
only if the program that extracts the image performs a low-level format as part of extracting
the data.  Further, it's only useful if the program on the disk actually pays attention to
the disk volume number.

The length of compressed chunks was added to the LZW/2 data to allow for partial recovery of
corrupted archives.  If a corrupted chunk was found, the extraction program could skip forward
4KB in the output file, and continue with the next chunk.  It's unclear which programs made
use of this feature.  Archives made by a certain Mac application have values stored in big-endian
order for this field, so it's usually best to ignore this value.

The length of the original file cannot be determined from the compressed data.  The file's
length is effectively rounded up to the nearest 4KB boundary as it is being compressed, and it
retains this form when expanded.  The code managing the expansion must be prepared to trim the
output.  Simply reading data until the codec halts will not yield correct results.

If presented with a zero-length file, the compressor could zero-fill a 4KB buffer and compress
that, avoiding a special case.  It's easier to just output no data at all, and have the
decompressor recognize a stream that ends after the file header as an empty file.  (This never
comes up in ShrinkIt, because it doesn't try to compress very small files.)

P8 and GS ShrinkIt add an extra byte to the end of LZW-compressed threads.  Because the
decompressor halts when all output is generated, rather than when all input is consumed, extra
trailing bytes are ignored.  Neither program appears to require the extra byte when expanding.
