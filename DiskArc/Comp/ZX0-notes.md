# ZX0 Compression Format #

This is fully documented on the project's web site: https://github.com/einar-saukas/ZX0

The algorithm is asymmetric, pairing slow compression with fast decompression, and is designed for
files that will be compressed on a modern system then decompressed on an 8-bit microcomputer.
It doesn't include a header or file length in the compressed output, but does output a stop symbol
at the end of the stream.

The format assumes the output begins with an encoded literal, and thus cannot represent a
zero-length file.  This must be handled at a higher level.
