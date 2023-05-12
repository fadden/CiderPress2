# GZip archive #

## Primary References ##

- RFC 1952: GZIP file format specification v4.3 (https://www.ietf.org/rfc/rfc1952.txt)
- https://en.wikipedia.org/wiki/Gzip

## General ##

While most people use gzip simply as a way to compress a single file, the format includes most of
the things you'd expect from a file archive, including the original filename, modification date,
OS identifier, file comment, and a CRC-32.  The specification allows multiple gzip files to be
concatenated together; when uncompressed, the output is the concatenation of the various "members".

The file format is designed to be created and unpacked from a stream, which means the file header
cannot hold the length of the file.  An indication of end-of-file must be embedded in the
compressed data stream.  One consequence of this is that it's not possible to store
uncompressed data in a gzip file.

Determining the compressed and uncompressed length from a gzip file is straightforward: seek to the
end to read the uncompressed length, and subtract the file header/footer sizes from the file's
total length to get the compressed size.  This falls apart completely if there are multiple gzip
members concatenated together, and will be inaccurate if the uncompressed data exceeds 4GB.  This
is expensive to do correctly, because the only reliable way to find where one member ends and the
next begins is to uncompress the data.

## File Layout ##

The overall layout is:
```
member 1
member 2
 ...
member N
```
In practice, files rarely have more than one member.

Each member starts with a header:
```
+$00 / 1: ID1 (0x1f)
+$01 / 1: ID2 (0x8b)
+$02 / 1: compression method (8=deflate)
+$03 / 1: flags; determines presence of remaining fields
+$04 / 4: modification date/time, UTC in UNIX time_t format
+$08 / 1: extra flags for use by compression codecs
+$09 / 1: operating system identifier
(if FEXTRA flag set)
  +$00 / 2: "extra" field length (does not include these length bytes)
  +$02 /nn: "extra" field
(if FNAME flag set)
  +$00 /nn: original file name, null terminated
(if FCOMMENT flag set)
  +$00 /nn: file comment, null terminated
(if FHCRC flag set)
  +$00 / 2: 16-bit CRC on header (computed as the low 16 bits of a CRC-32)
```
The compressed data immediately followed the header.  There is no notion of uncompressed
data storage.  A zero-length file generates 0x03 0x00.

An 8-byte footer follows the compressed data:
```
+$00 / 4: CRC-32
+$04 / 4: unsigned size of the original (uncompressed) input data, mod 2^32
```
Files larger than 2^32 may be stored in gzip, but the size value won't match the contents.

### Filenames and Comments ###

The specification mandates ISO-8859-1 characters for filenames and comments.  Filenames must
be forced to lower case if the original file resides on a case-insensitve filesystem.  (But
see notes about the gzip utility, below.)  The end-of-line character in comments is a single
linefeed (0x0a).

The stored name is just the filename, not a partial path.  In practice, the embedded filename
is rarely used, as most people expect the extracted file to simply lose the ".gz" extension from
the archive name.

## gzip Utility ##

The GNU "gzip" program is the canonical utility.  The only optional header field it adds is the
original filename, and if the input is streamed it won't even add that.  It may be unwise to
use the other optional fields, simply because most consumers of gzip files will not have seen
them before.

The program performs minimal manipulation of the filename, so on systems that use UTF-8 filenames
(e.g. Linux) the filename will be stored with UTF-8 encoding.

The file modification date stored in the archive is not used when extracting files.  Instead,
the modification date is copied from the input file to the output file, when compressing or
decompressing.
