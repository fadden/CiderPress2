# Apple Pascal Files #

File types:
 - PCD ($02) / any : Apple Pascal codefile
 - PTX ($03) / any : Apple Pascal textfile

Primary references:
 - _Apple II Pascal 1.3_, https://archive.org/details/apple-ii-pascal-1.3
   (.TEXT and .CODE described on page IV-16 and IV-17)

The file type note index defines ProDOS type PCD as "Apple /// Pascal code" and PTX as "Apple ///
Pascal text".  Treating them as equivalent to the Codefile and Textfile files created by Apple
Pascal on the Apple II may or may not be correct.  However, this seems like the natural thing
to do when copying Pascal files to a ProDOS disk.

## Textfile ##

The file is divided into 1KB chunks.  The first chunk is reserved for use by the system text
editor.  The contents are not documented in the Apple references, but some information is
available (e.g. https://markbessey.blog/2025/05/08/ucsd-pascal-in-depth-3-n/).  The chunk contains
housekeeping data, such as margins and timestamps.  The Pascal system Editor program seems
perfectly happy to open files with a zeroed-out header.

The remaining chunks contain a series of unbroken lines of ASCII text, each of which is
terminated with a carriage return ($0d).  Any leftover space at the end of a chunk is filled
with NULs ($00).

Pascal programs are often indented with spaces (' '), so to reduce the file size, leading spaces
may be compressed with run-length encoding.  If a line starts with an ASCII DLE ($10), the
following byte holds the number of spaces, plus 32.  It's valid to encode a lack of indentation
as $10 $20 (i.e. 0 spaces).  The maximum number of spaces that can be encoded this way is not
documented, but the Editor appears to stop at $6d (77 spaces), and staying at or below
$7f (95 spaces) seems prudent.

It's unclear to what extent control characters and high-ASCII text are allowed or tolerated,
though the Editor does not allow control characters to be entered (they're inserted as '?'
instead).

According to the Pascal 1.3 manual, the name of every textfile must end in `.TEXT`.

Newly-created textfiles will be 2KB: 1KB for the header, 1KB for the first (empty) text chunk
(which will, if created by the Editor, have a single carriage return in it).

## Codefile ##

A codefile may be any of the following:
 - Linked files composed of segments, ready for execution.
 - Library files with units that may be used by other programs.
 - Unlinked files created by the compiler.

All codefiles have a Segment Dictionary in block 0, followed by a description of 1-16 segments.
Segments may be code or data, and may be up to 64 blocks (32KB) long.

The segment dictionary has fixed slots for all 16 segments, which are stored as a consecutive
series of arrays:
```
+$000 / 64: 16 sets of 16-bit code length and 16-bit code address
+$040 /128: 16 sets of 8-character ASCII segment name (padded with spaces on the end)
+$0c0 / 32: 16 sets of 16-bit segment kind enumeration
+$0e0 / 32: 16 sets of 16-bit text address (for regular/Intrinsic Units)
+$100 / 32: 16 sets of segment info: 8-bit segment number, 8-bit type+version
+$120 /  4: bitmap that tells the system which Intrinsic Units are needed
+$124 /220: (library information, format undefined)
```
There may be empty slots; it's possible for slot 0 to be empty, even for an executable program
(see SYSTEM.FILER).  Empty slots can be identified by testing for CODEADDR and CODELENG both
equal to zero.  (It's normal for DATASEG segments to have address 0.)

All multi-byte integers are stored in little-endian order.

Some of the segment types have a documented structure.  See the Apple Pascal references for
details.
