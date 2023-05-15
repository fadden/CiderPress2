# "Squeeze" Compression Format #

## Primary References ##

- Original public domain SQ/USQ code (e.g.
  https://www.tuhs.org/Usenet/comp.sources.unix/1984-December/002540.html )

## General ##

The "Squeeze" format was developed by Richard Greenlaw around 1981.  His program compressed
files with a combination of run-length encoding and Huffman encoding, the latter used to encode
bytes based on their frequency across the entire file.  The program was commonly used on CP/M,
MS-DOS, UNIX, and other platforms in the early 1980s.

On the Apple II, a port of the programs called SQ3/USQ2 was written by Don Elton.  These could
be used to compress individual files.  The code was later integrated into the Binary II
Library Utility (BLU), which could compress files as they were being added to an archive.

The original versions of the program added ".SQ" to the filename, or inserted 'Q' into the middle
of the filename extension, e.g. changing "FILE.TXT" to "FILE.TQT".  The Apple II utilities
appended ".QQ" to the filename.

## Detail ##

The run-length encoder will transform any run of more than two bytes to:

  `<value> <delim> <count>`

Where 0x90 is the chosen delimiter.  If the delimiter itself appears in the file, it will be
escaped by following it with a count of zero.  A run of delimiters is a worst-case scenario,
resulting in a 2x expansion.

A frequency analysis pass is performed on the output of the run-length encoder to create the
Huffman encoding tree.  By its nature, the compression algorithm requires two passes through
the complete file, so it is unsuited for streaming.

The file includes a simple checksum to verify data integrity, and the compressed data ends with
a stop symbol.  The size of the original data file is not stored, so the only way to know how
big the file will get when uncompressed is to uncompress it.

The byte frequencies are scaled in such a way that the longest possible output symbol is 16 bits.
This allows some of the code to be a little simpler.

## File Layout ##

All multi-byte values are stored in little-endian order.

The "standalone" file format, used by various utility programs, begins with a header:
```
+$00 / 2: magic number ($76 $ff)
+$02 / 2: checksum
+$04 /nn: null-terminated ASCII string with original filename; may be a full pathname
```

When used within a NuFX archive, the header is omitted.  The file continues:
```
+$00 / 2: tree node count
+$02 /nn: tree nodes, four bytes each (16 bits left child, 16 bits right child)
 ...
+$xx    : start of compressed data
```

Nodes in the binary tree don't have values, exactly, just a pair of integers for the left and
right children.  The value may be positive, indicating a reference to another node, or negative,
indicating a literal value.  Node references are simply indices into the linear node array.
Literal values were offset by one to allow the value 0 to be output (there's no "negative 0"
in 2's complement math), so negate and add 1 to get the byte value.  End-of-file is indicated
by code 256.

If the file is completely empty, the code outputs a tree with zero nodes, and does not generate
any compressed data (no end-of-file marker).  BLU doesn't try to compress small files, and SQ3
crashes on empty input files, so this may not be relevant for Apple II files.
