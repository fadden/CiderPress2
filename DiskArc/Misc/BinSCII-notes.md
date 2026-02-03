# BinSCII Encoding #

## Primary References ##

- David Whitney's posts in comp.sys.apple, February 1989:
  https://groups.google.com/g/comp.sys.apple/c/BT_GGUlL8o4/m/rwZ2ynJpReAJ
- "binscii.5", an unofficial specification written by Neil Parker, based on `sciibin` code

## General ##

Usenet and some e-mail systems aren't 8-bit clean, so binaries posted to forums or attached to
mail messages may need to be converted to a 7-bit format.  It may also be necessary to avoid
certain strings, e.g. the system might have to modify your message if a line starts with "From: "
or is too long.  A further complication arises when messages are passed through a network that
uses a non-ASCII character set, such as EBCDIC, which could lack punctuation characters like
'^' and '{'.

A simple approach to work around these issues is to generate a hex dump, with two hexadecimal
digits per byte, but this doubles the size of the file.  This can by improved by mapping three
8-bit values to four 6-bit values, outputting 64 different symbols.  Implementations commonly used
in the 1980s and 1990s included [uuencode](https://en.wikipedia.org/wiki/Uuencoding) on UNIX
systems and [BinHex](https://en.wikipedia.org/wiki/BinHex) on the Macintosh.  A standard for
Base64 encoding was published in [RFC 4648](https://www.rfc-editor.org/rfc/rfc4648) in 2006.

In 1989, David Whitney [developed](https://groups.google.com/g/comp.sys.apple/c/pLZaErOIwPY/m/azcRAeCEtCwJ)
the BinSCII format as a way to encode Apple II files.  It explicitly supports multi-part
archives, includes the decoder alphabet in the header to work around character set conversion
issues, and provides a way to encapsulate ProDOS file attributes.  It quickly became the default
way to distribute Apple II files on Usenet and via e-mail.

Files in BinSCII format generally use the ".bsc" or ".bsq" extensions, the latter implying that
the file contains a ShrinkIt archive.

## File Structure ##

When encoding a file, the source is split into 12KB chunks, each of which is encoded independently.
The chunk size could theoretically be larger or smaller, and could vary between chunks, but most
decoders would be confused to find anything other than 12288 bytes in every chunk except the last.

The chunks look like this:

```
[Optional human-readable notes about what this file is.]

FiLeStArTfIlEsTaRt
ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789()
HSHRINKIT       A4ZyAAAAA8)4RJAI2CHA3UxAOIjtAADAA0Y7
a(voNqXqpOg8z3YIlm0ADQfjNmVqpOA8x3o(M04AO0IwvJKwNCQqdOQsK)LAQfF4
A3K(CB)(wDeyhnsPiqD8b1LAGAPIEAQn1DN6rgII)CAIgQVZAAABAAAA1aLAyC7w
[...]
jAGeAkMfAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
A8mR
[other text may follow]
```

The first line is the BinSCII chunk signature ("FiLeStArTfIlEsTaRt").  The second line is the
64-character encoding dictionary.  The third line holds the filename and file attributes:

```
+$00 /  1: filename length (1-15) + $40 (this is not encoded with the dictionary)
+$01 / 15: ProDOS filename (ASCII A-Z and '.' only), padded with spaces
+$10 / 36: encoded file attributes
```
The 36 bytes of encoded attributes become 27 bytes:
```
+$00 / 3: total length of file
+$03 / 3: offset in bytes of this segment (should be a multiple of 12288)
+$06 / 1: ProDOS access byte
+$07 / 1: ProDOS file type
+$08 / 2: ProDOS aux type
+$0a / 1: ProDOS storage type
+$0b / 2: ProDOS block count
+$0d / 2: ProDOS creation date
+$0f / 2: ProDOS creation time
+$11 / 2: ProDOS modification date
+$13 / 2: ProDOS modification time
+$15 / 3: length, in bytes, of this segment (should be [1,12288])
+$18 / 2: CRC-16/XMODEM of preceding fields
+$1a / 1: (reserved)
```

All multi-byte integers are in little-endian order.  The ProDOS attributes are in the same order
and format used by the MLI GET/SET_FILE_INFO calls, and should be the same in every chunk.  The
CRC does not include the filename, because the author wanted people to be able to edit it directly
in the encoded file.  This potentially makes it vulnerable to corruption, but it's obvious and
easy to fix.

The data lines start immediately after the header.  Each line is 64 characters long, and may end
with CR, LF, or CRLF.  Some files appear to be indented with whitespace, but not all decoders
handle this.  The data section must not include blank lines or other extraneous characters,
and lines may not be split.

The 8:6 ratio means each 64-character line contains 48 bytes from the original file.  If the
input file isn't a multiple of 48 bytes, additional zeroes are added as padding when the last
data line is encoded.

The last line of the chunk holds a CRC-16/XMODEM of the decoded data, followed by a zero byte,
encoded as a 4-character string.  The CRC includes the padding added at the end of the file.

Bytes are encoded by taking three input bytes:

    abcdefgh ijklmnop qrstuvwx

And shifting them around to get four values:

    00stuvwx 00mnopqr 00ghijkl 00abcdef

The values are used as indices into the encoding dictionary to get the output characters.

Encoded segments may be transmitted singly or concatenated together, in any order.  This was
especially useful when posting to systems with tight restrictions on file sizes.  It is the
responsibility of the decoding software and/or end user to ensure that all segments are present.
There is no full-file checksum.

## Software ##

David Whitney's `BinSCII`, written for ProDOS 8, was the original implementation.

Marcel J. E. Mol wrote the `sciibin` decoder for UNIX systems.

On the Apple IIgs, the `GScii+` NDA could unpack BinSCII, uuencode, and MacBin files.

While BinSCII can preserve ProDOS file attributes, it's generally better to store files in a
ShrinkIt archive before encoding them, because it may otherwise be difficult to tell if you've
successfully decoded all file segments.  The primary exception to this rule is the distribution
of ShrinkIt itself.

While it's theoretically possible to encode resource forks directly in BinSCII by setting the
storage type, most (if not all) decoders won't or can't handle them, so storing forked files in
ShinkIt archives is recommended.
