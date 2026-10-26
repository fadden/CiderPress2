# RLE-90 Compression #

## Primary References ##

- Source documentation for The Unarchiver:
  https://code.google.com/archive/p/theunarchiver/wikis/Rle90Algorithm.wiki

## Algorithm ##

This is a simple run-length encoding algorithm used by Arc and StuffIt.  It's also part of the
[Squeeze](Squeeze-notes.md) algorithm.  It's very fast, but ineffective on most types of files.

In brief:
 - Non-run data is just output as plain bytes.
 - If a run of 3 or more bytes is detected, the value is output, followed by 0x90, followed
   by a byte with the total run length.  The length includes the byte previously output,
   so the value is effectively (count - 1).  This allows runs up to 255 bytes in length to
   be represented as a 3-byte sequence.
 - When the input is 0x90, the sequence 0x90 0x00 is output.

It is not possible to encode a series of delimiter bytes as a run, so the worst-case
expansion is 2x.

Because count-1 is used, the sequence 0x90 0x01 is useless and should never appear, and
the sequence 0x90 0x02 would represent a two-byte run with three bytes.

We do not expect to see 0x90 followed by a count at the start of a file, since that would
represent a run of undefined bytes.
