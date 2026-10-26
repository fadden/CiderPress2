# PackIt/StuffIt Huffman Compression #

## Primary References ##

- Source code for PackIt, retrieved (via archive.org) from network172.com

## General ##

As noted in the StuffIt v1.5 user manual:
> Huffman used by StuffIt is the same algorithm as used by PackIt II except that it’s performed on
each fork individually.

The [PackIt](https://en.wikipedia.org/wiki/PackIt) application was written for the Macintosh
by Harry R. Chesley in 1986.  It collected a group of files into a single archive, and applied
compression to the entire archive.  The application was widely used until it was supplanted by
StuffIt in 1987.

StuffIt 1.5 was able to unpack PackIt archives, and used the same algorithm for compression
method #3 in its own archives.

## Details ##

The algorithm operates in two passes.  The first scans the file contents, counting up the number
of times each byte value appears.  The counts are used to generate a Huffman prefix tree, and
the second pass reads the file and outputs the encoded form.

The output is a bit stream, with bits output to the high bit first.  The data contains a
description of the tree, followed immediately by the encoded data.  The tree is represented as
a hierarchy in depth-first order, decoded like this:

 - get next bit
 - if bit is 1, this is a leaf node; read the next 8 bits to get the symbol value
 - if bit is 0, this is a middle node; recurse to handle the left tree, then the right tree

The tree will require up to 320 bytes to represent: 256 bytes for the values, 32 bytes (256
bits) for the 1 bits before each value, and 32 bytes (256 bits) for the 0 bits before each
middle node in a degenerate tree.  In practice, the actual size depends on the number of
different symbols in the tree and the complexity of the distribution.

The encoded data is just a bit stream with Huffman sequences.  There is no end-of-stream marker,
so decompression continues until the expected amount of data has been produced.  Input of length
zero is not handled by the algorith, but returning zero bytes of output is reasonable.
