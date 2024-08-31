# LZC Compression Format #

## Primary References ##

- Source code for UNIX `compress` command, version 4.0

## General ##

For several years, the [`compress` command](https://en.wikipedia.org/wiki/Compress_(software))
was the primary way to compress files on UNIX systems.  It used an algorithm based on
Lempel-Ziv-Welch (LZW) [encoding](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Welch),
which was faster and had better compression ratios than previous programs like `pack` and
`compact`, which used RLE and Huffman encoding.  The specific implementation of the algorithm
is sometimes referred to as `LZC`.

`compress` marked its files by adding `.Z` to the filename.  It was largely supplanted by `gzip`,
which has better compression ratios and wasn't subject to Unisys patents.

The program went through a few iterations, with primary development ending in 1985 with
the release of version 4.0.  Various minor versions were released by different authors, generally
to improve compatibility with specific systems, or to tweak the way clear codes were issued.

The maximum width of the LZW codes, which affects how much memory is required, could be
configured at compile time and overridden to be lower at run time.  The value could be set
between 9 and 16, inclusive.  This impacted decompression, meaning that an implementation limited
to 12-bit codes could not decompress a file that used 16-bit codes.

GS/ShrinkIt can decompress NuFX threads compressed with LZC, up to 16 bits.  It does not support
compression in that format, but it is possible to create such archives with NuLib.

## Detail ##

Files start with the magic number $1F $9D.  This is followed by a byte with compression
parameters: the low 5 bits hold the maximum code length (9-16), and the high bit holds a
"block compress" flag that determines whether block clear codes are issued or expected.  (Block
clear codes were added in v3 as a non-backward-compatible change.)

The header is followed by the LZW-encoded data.  There is no in-stream indication of end of
file; the decompressor just reads data until it runs out.  There is no checksum.

Internally, the compression code fills a buffer with 8 codes before writing output.  Codes start
at 9 bits and grow to 16, so if we're currently working with 10-byte codes we'll be writing 10
bytes at a time.  When the code size changes, the entire buffer must be flushed, because the
decompression side also reads the input in 8-code chunks.  When operating in "block mode", each
transition to a new code with happens to occur at a multiple of 8 codes, so there are no
alignment gaps in the output unless a block clear code is emitted.  With the older (v2) behavior,
the clear code is not reserved, which increases the number of available 9-bit codes by 1, so a gap
will appear at the first code width change.  This behavior, and the somewhat convoluted
implementation in `compress` v4.0, has led to [bugs](https://github.com/vapier/ncompress/issues/5)
in some implementations.

The only time a partial chunk is written is at the end of the file.
