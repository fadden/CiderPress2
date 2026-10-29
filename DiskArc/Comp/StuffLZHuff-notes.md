# StuffIt LZ+Huffman Compression #

## Primary References ##

- The source code for "unar", notably:
  https://github.com/ashang/unar/blob/master/XADMaster/XADStuffIt13Handle.m

## General ##

StuffIt's compression method 13, referred to as "LZ+Huffman", combines an LZ77 compressor with
fixed Huffman encoding.  The algorithm is proprietary to StuffIt and has not been published,
but it has been reverse engineered.

## Details ##

The first byte in the compressed data stream determines the parameters used by the codec.  The
value has the form `IIIIFSSS`, where `IIII` is the table index (0-5).  Indices 1-5 indicate that
a set of hard-coded tables should be used, while index 0 tells the decoder that the encoding
tables should be read from the file itself.  When index is zero, the `F` bit indicates whether
the file holds one or two literal/length tables, and `SSS` determines the number of symbols
in the offset-encoding table.

If the encoding tables are contained within the file, they come next.  The tables are themselves
encoded with Huffman, using a hard-coded table.

The literal/length tables have 321 symbols.  Entries 0-255 are used to encode literals, entries
256-319 are used to encode match lengths, and entry 320 is used to mark the end of the stream.

To decode a file, set the current decoder to the "first" 321-symbol tree, and loop until EOF:
 - Decode the next symbol using the current tree.
 - If the value is 320, we've reached the end of the file.  Stop.
 - If the value is < 256, we have a literal.
   - Make the "first" tree the current tree.
   - Output the value, updating the sliding window contents.
 - If the value is >= 256, we have a match.
   - Make the "second" tree the current tree.
   - Get the match length from the value:
	 - If value is in the range [256,317], set length to `(value - 256 + 3)` (range 3 - 65).
	 - If value is 318, read the next 10 bits from the input, and set
	   `length = bits + 65` (max 1088).
	 - If value is 319, read the next 15 bits from the input, and set
	   `length = bits + 65` (max 32832).
   - Decode the next symbol using the "offset" code tree.
	 - If 0, set offset=1.
	 - If 1, set offset=2
	 - Otherwise, the decoded value indicates the length, in bits, of the match offset.  To get
	   the match length, read `(value - 1)` bits from the input, add to it the next power of 2,
	   and add one.  For example, if the decoded value is four, the offset would be
	   `(next 3 bits from input) + (1 << 3) + 1`.  In other words, it's `(offset - 1)` stored in
	   the stream, using the fewest bits possible, without the most significant '1' bit.
   - Output the match, updating the sliding window contents.

The match offset specifies the distance back from the current position (so an offset of
zero wouldn't make sense).  The sliding window should be 64KB (the next power of 2 above the
longest possible match length), so the offset is at most 16 bits.  However, the actual maximum
offset is limited by the number of symbols in the offset code tree, which varies from 11 to 14
in the hard-coded tables, and can be from 10 to 17 in the file-stored table.

Bits are read from bytes starting with the least-significant bit.

Files are handled as a continuous stream, with no block boundaries.  This means the symbol
probability distributions are fixed for the entire file, making compression less effective on
files that contain different types of data in different sections.

### Unpacking File-Stored Tables ###

The various tables are encoded with a "meta code" tree that has 37 symbols.  The tree is generated
from arrays that hold the symbol codes and lengths.

The encoding trees are again stored as a list of lengths, one per symbol, but they are encoded in
the file in a way that allows runs of identical lengths to be stored efficiently.  The tables are
decoded like this:

 - Set "length" to zero.
 - While symbols remain (e.g. 321 for the literal/length tables):
   - Decode the next symbol using the "meta" code tree.
   - If value is [0,30], set length to `(value + 1)`.
   - If value is 31, set length to -1.
   - If value is 32, increment length.
   - If value is 33, decrement length.
   - If value is 34, read the next bit from the stream.  If the bit is 1, output length.
   - If value is 35, read the next 3 bits from the stream.  Output length N+2 times (2 - 9).
   - If value is 36, read the next 6 bits from the stream.  Output length N+10 times (10 - 73).
   - Output length.

This generates an array with length values for each symbol, which can then be fed into the
routine that creates a Huffman tree from an array of lengths.

If the `F` flag in the first byte of the file is 1, the "first" and "second" trees share the same
definition.  If the `F` flag is 0, the second tree is stored immediately after the first.

The number of the symbols in the "offset" tree is determined by adding 10 to the low three bits
(`SSS`) of the value found in the first byte of the file.
