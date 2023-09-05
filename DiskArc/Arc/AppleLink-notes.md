# AppleLink-PE Package Format (ACU) #

## Primary Sources ##

 - Reverse engineering
 - Archive list code by Andrew Wells: https://github.com/fadden/CiderPress2/discussions/4

## General ##

[AppleLink](https://en.wikipedia.org/wiki/AppleLink) was an online service created by Apple
Computer.  Initially only available to employees and dealers, it was eventually opened to
software developers.  This was to be replaced by a system available to all users, called AppleLink
Personal Edition, but for various reasons this was renamed to America Online.

Files were stored in AppleLink Package Compression Format, which combined the data and resource
forks and file attributes into a single file.  This appears to have been a Macintosh-specific
format.

A separate format was designed for Apple II files on AppleLink Personal Edition.  These were
created and unpacked with the AppleLink Conversion Utility (ACU), written by Floyd Zink (who also
wrote BLU, the Binary II Library Utility).  The file format was assigned file type LBR/$8001, but
no file type note was published.  Files typically end in ".ACU".

Files in ACU archives can be stored with or without compression.  The only supported algorithm
is SQueeze, a combination of RLE and Huffman encoding.  The format is the same as is used by
Binary II, but without the filename header.

## File Structure ##

Files have a header that identifies the file format, followed by a series of records.  Each
record has a header with the file attributes and CRCs.  The record contents generally reflect
the GS/OS view of file attributes, with 16-bit file types and 32-bit aux types.  All
multi-byte integers are stored in little-endian order.

The file header is 20 bytes:
```
+$00 / 2: number of records in archive
+$02 / 2: source filesystem ID
+$04 / 5: signature "fZink"
+$09 / 1: ACU version number ($01)
+$0a / 2: length of fixed-size portion of file headers ($36)
+$0c / 7: reserved, must be zero
+$13 / 1: ? $DD
```

The header before each file is:
```
+$00 / 1: resource fork compression method
+$01 / 1: data fork compression method
+$02 / 2: checksum of resource fork contents
+$04 / 2: checksum of data fork contents
+$06 / 4: blocks required to store resource fork on ProDOS
+$0a / 4: blocks required to store data fork on ProDOS
+$0e / 4: length of resource fork in archive (compressed)
+$12 / 4: length of data fork in archive (compressed)
+$16 / 2: ProDOS access flags
+$18 / 2: ProDOS file type
+$1a / 4: ProDOS aux type
+$1e / 2: ? reserved, must be zero
+$20 / 2: ProDOS storage type; $0d indicates directory
+$22 / 4: uncompressed length of resource fork
+$26 / 4: uncompressed length of data fork
+$2a / 2: create date
+$2c / 2: create time
+$2e / 2: modification date
+$30 / 2: modification time
+$32 / 2: filename length
+$34 / 2: checksum of file header
+$36 /nn: filename
```
The file header is immediately followed by the resource fork contents, and then the data fork
contents.  If there are additional records, the file header follows immediately.  There is no
padding for alignment.

The filename field holds a partial path.  For ProDOS, the components are separated by '/'.  It's
unclear how other filesystems would be handled.  Directories are stored explicitly, though it's
not known if storing them is mandatory.

The compression method is $00 if the data is uncompressed, $03 for SQueeze.

Dates and times are in ProDOS-8 format.

The only Binary II field that doesn't appear here is "native file type", which could hold the
raw DOS 3.3 file type.  It's possible the reserved field at $1e was set aside for that.

The operating system ID comes from the GS/OS FST definition.  In practice it will usually be $01
(ProDOS), though it could reasonably be $06 (HFS).

The 16-bit checksums on the record headers and file data have an unknown format.  The values do
not match any of the commonly-used CRC-16 calculations or a simple checksum.
