# LZHUF Compression Format #

## Primary References ##

- Source code for LZHUF.C (http://cd.textfiles.com/rbbsv3n1/pac4/okumura.zip)

## General ##

LZHUF was developed by Haruyasu Yoshizaki as a faster alternative for Haruhiko Okumura's LZARI.
Both were distributed in the late 1980s on a Japanese computer network called PC-VAN.

The algorithms derive from LZSS, which is in the LZ77 family of algorithms.  LZHUF modifies LZSS
to use adaptive Huffman encoding to compress character literals and match lengths, and static
Huffman encoding on the upper 6 bits of the 12-bit pointers.

Various improved versions of the algorithm were devised over the next few years, the most
popular of which was the Deflate algorithm used by PKZIP, which was eventually standardized
in RFC 1951.

The output of LZHUF has no file signature or other header information.  The file starts with a
32-bit little-endian length of the original file, followed immediately by compressed data.  The
data stream doesn't have an "end" symbol, so it's necessary to use the length of the uncompressed
data to know when to stop.  Storing the length at the start of the output prevents compression
from being streamable.
