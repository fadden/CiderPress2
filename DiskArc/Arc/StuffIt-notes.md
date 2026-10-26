# StuffIt Archive Format #

## Primary References ##

- StuffIt 1.5 User's Guide
- Wiki archive for "theunarchiver": https://code.google.com/archive/p/theunarchiver/wikis
- The source code for "unar", notably:
  https://github.com/ashang/unar/blob/master/XADMaster/XADStuffItParser.m and
  https://github.com/ashang/unar/blob/master/XADMaster/XADStuffIt5Parser.m

## General ##

[StuffIt](https://en.wikipedia.org/wiki/StuffIt) is a data archiving application developed for
the Macintosh by Raymond Lau.  Development began in mid-1987.  The program was distributed as
shareware until version 1.5.1 in 1988, after which the program was split into StuffIt Classic
(shareware) and StuffIt Deluxe (commercial), with the latter published by the newly-formed Aladdin
Systems.  A limited version of the application that could only unpack archives, called StuffIt
Expander, was bundled with Mac OS for several years.  The program was published in various
incarnations until it was discontinued in 2020.

The file format for v1.5 was described in detail in the User's Guide, but the (significant) changes
to later versions of the program, notably v5.5, have not been published.  A major update, the
"StuffIt X" format, was introduced in 2002.  Some of the compression algorithms used are similar
or identical to those used in other programs, but others are unique.

StuffIt archives found on the Internet typically use the ".sit" extension.  StuffIt X files
use ".sitx", and self-extracting archives use ".sea".  On HFS, archives used the creator `SIT!`
with file type `SIT!` or `SITD`.

Support for various compression algorithms was added over the years.  Early versions supported
RLE, simple Huffman, and LZC.  Later versions included arithmetic encoding, LZH, BWT, and others.

Because the format is proprietary, very few programs (other than those developed by Aladdin or
its successors) can unpack post-1.5 StuffIt archives.  One of the few is
"[The Unarchiver](https://theunarchiver.com/command-line)", an open-source project based in
part on an Amiga unarchiving system called [XAD](https://en.wikipedia.org/wiki/XAD_(software)).

## File Structure (v1.5) ##

StuffIt archives begin with a fixed-length archive header, followed by file records.  Each record
starts with a fixed-length file header, followed by the compressed resource fork data, and then
the compressed data fork data.

All multi-byte values are in big-endian order.

Archive header (22 bytes):
```
+$00 / 4: signature ("SIT!")
+$04 / 2: number of files at the top level of the archive
+$06 / 4: length of entire archive file, including this header
+$0a / 4: signature2 ("rLau")
+$0e / 1: version number (1 for &lt;= v1.5, 2 for 1.6 - 4.5)
+$0f / 7: reserved
```

File header (112 bytes):
```
+$00 / 1: resource fork compression method
+$01 / 1: data fork compression method
+$02 /64: filename, stored as a length byte followed by text
+$42 / 4: file type
+$46 / 4: creator
+$4a / 2: Finder flags (from FInfo block)
+$4c / 4: creation date
+$50 / 4: modification date
+$54 / 4: uncompressed resource fork length
+$58 / 4: uncompressed data fork length
+$5c / 4: compressed resource fork length
+$60 / 4: compressed data fork length
+$64 / 2: CRC-16/ARC of uncompressed resource fork
+$66 / 2: CRC-16/ARC of uncompressed data fork
+$68 / 1: rPad (used for encrypted entries)
+$69 / 1: dPad (used for encrypted entries)
+$6a / 4: reserved
+$6e / 2: CRC-16/ARC of previous bytes in this header
```

Filenames use the Macintosh character set, e.g. Mac OS Roman.

Timestamps are in HFS/MFS format, i.e. unsigned 32-bit values indicating the time in seconds since
midnight on Jan 1, 1904, in local time.

The meaning of the compression method field is described in a later section, but note that
bit 4 (the value 16) indicates an encrypted entry, while methods 32 and 33 are used to indicate
"startFolder" and "endFolder", respectively.

The resource fork is used to store file comments, in a plain-text `SitC` resource.

### Hierarchy Maintained Folder (HMF) ###

File headers have a fixed-width field for the filename, which is fine for a flat filesystem but
comes up short on a system with subdirectories.  StuffIt 1.5 added the ability to preserve
directory layouts, using a feature called Hierarchy Maintained Folder (HMF).

HMF creates a hierarchical structure within the archive.  Folders are represented as a
"startFolder" entry, followed by the records for the files contained within the folder, and
ending with an "endFolder" entry.  The data between the start/end entries is essentially a
complete StuffIt archive without the archive header.

The "startFolder" entry has compression type 32, the "endFolder" entry has compression type 33.
The resource fork lengths are set to zero.  The uncompressed data fork length is set to the sum
of the uncompressed lengths of all forks in all entries, and the compressed data fork length is
set to the total length of data contained by all entries in the folder.  This system maintains
backward compatibility: an older version of StuffIt would see the unknown compression types, and
skip over the folder and its contents entirely.  The `numFiles` entry in the archive header
counts each top-level folder as a single entry for similar reasons.

Subdirectories are handled by creating folder entries inside other folder entries.


## File Format (v5) ##

The file format generated by version 5 and later versions of StuffIt is proprietary.  Some
information about this format has been determined through reverse-engineering, primarily by the
contributors to the XAD/unar utility.

The structure has a similar feel to v1.5, with entries arranged in a hierarchical structure.

Archive header (100 bytes):
```
+$00 /80: header, text string starting with "StuffIt (c)1997-"
+$50 / 2: (unknown)
+$52 / 1: version (must be 5)
+$53 / 1: flags
+$54 / 4: total length of archive file
+$58 / 4: (unknown)
+$5c / 2: number of entries in archive
+$5e / 4: offset to first entry (i.e. size of this header, including optional fields)
+$62 / 2: CRC/ARC of archive header? (full header with this field zeroed?)
```

The `flags` field is a bit vector:
```
  $10 - if set, archive header has an additional 14 bytes (unknown purpose)
  $20 - if set, archive header has a comment; 16-bit length and buffer length appended to header
  $40 - if set, archive header has a 16-bit count, followed by that many 20-byte structs
  $80 - if set, archive is encrypted (8-bit hash size appended to header)
```

If (flag & 0x10), advance 14 bytes.  The meaning of this section is unknown.

If (flag & 0x20), a comment for this entry is present.  Two lengths are stored.  This will be at
offset $64 or $72:
```
+$72 / 2: comment length
+$74 / 2: comment pad length (i.e. unused space in the comment area)
```

If (flag & 0x80), the archive is encrypted.  The next byte in the header is the length of the
hash data in bytes - should be 5 - followed immediately by that many bytes of data.

If (flag & 0x40), the header contains a 16-bit count, followed by that many instances of a
20-byte structure.  Purpose unknown.

Finally, if a comment was noted earlier, the comment text will appear, followed by some pad
bytes so the archive doesn't have to be resized if the comment gets shorter or a little longer.
Comments use the Mac OS character set (not UTF-8), with carriage returns ($0d) as line separators.

Applications that aren't interested in the optional fields can just read the fixed-length header
and then seek to the first entry.

The archive header is followed by a series of entries for files and folders.  These records
are variable length.

Each record has two main parts:
```
+$00 / 4: signature ($a5 a5 a5 a5)
+$04 / 1: version
+$05 / 1: (unknown)
+$06 / 2: length of first section of this header
+$08 / 1: (unknown)
+$09 / 1: record type / flags
+$0a / 4: creation date
+$0e / 4: modification date
+$12 / 4: file offset of previous entry, or 0 if first entry
+$16 / 4: file offset of next entry, or 0 if last entry
+$1a / 4: file offset of folder entry, or 0 if not inside a folder
+$1e / 2: filename length
+$20 / 2: header CRC?
+$22 / 4: uncompressed data fork length (for folder, is ?)
+$26 / 4: compressed data fork length (for folder, is sum of contained files?)
+$2a / 2: data fork CRC
+$2c / 2: (unknown)
(if folder)
  +$2e / 2: number of files
  (if data fork length is $ffffffff, stop here)
(if regular file)
  +$2e / 1: data fork compression method
  +$2f / 1: password key length (should be 5)
  +$30 / 5: password key data
+$xx /nn: filename (UTF-8)
(check header length field to see if next section exists)
  +$xx / 2: comment buffer length
  +$xx / 2: (unknown)
  +$xx /nn: comment data
```

This is immediately followed by a second section:
```
+$00 / 2: more flags; if bit $01 is set, a resource fork is present
+$02 / 2: (unknown)
+$04 / 4: file type
+$08 / 4: creator
+$0c / 2: Finder flags
+$0e /??: gap of 18 or 22 bytes, based on version
(if has resource fork)
  +$xx / 4: uncompressed resource fork length
  +$xx / 4: compressed resource fork length
  +$xx / 2: resource fork CRC
  +$xx / 2: (unknown)
  +$xx / 1: resource fork compression method
  +$xx / 1: password key length (should be 5)
  +$xx / 5: password key data
```

Archives generally don't have resource forks.


## Self-Extracting Archives ##

Some versions of StuffIt can create self-extracting archives, which were customarily wrapped with
MacBinary or BinHex to preserve the file type data.  The ".sea" format is not documented.


## Compression Algorithms ##

Only methods 1-3 were supported by StuffIt v1.5.1, but later algorithms do appear in archives with
the v1.5 layout.  Method 13 seems to be fairly common in both v1.5 and v5 archives.

- 0: (uncompressed)
- 1: [RLE-90](../Comp/RLE90-notes.md).
- 2: [LZC](../Comp/LZC-notes.md). Output matches the old "compress" command, but the three-byte
     header is omitted.  It always uses max 14-bit codes with block compression enabled.
- 3: Huffman. Simple byte-based Huffman encoding.
- 5: LZAH.
- 6: Fixed Huffman.
- 8: MW (an LZW variant)
- 13: LZ+Huffman.  A custom LZ77 + fixed huffman scheme.
- 14: "Installer"
- 15: Arsenic (RLE + BWT + arithmetic encoding).
- 32: (marks start of a folder)
- 33: (marks end of folder)

Bit 4 (value=16) is set if the entry is encrypted.

StuffIt v1.5 tried RLE, LZC, and Huffman, and kept whichever worked best.  Later versions switched
to LZ+Huffman, which generally outperforms the others.  StuffIt v5 archives seem to use either
LZ+Huffman or Arsenic.
