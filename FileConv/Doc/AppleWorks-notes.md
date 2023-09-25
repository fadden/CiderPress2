# AppleWorks "Classic" Documents #

File types:
 - ADB ($19) / any : AppleWorks Data Base
 - AWP ($1a) / any : AppleWorks Word Processor
 - ASP ($1b) / any : AppleWorks Spreadsheet

Primary references:
 - Apple II File Type Note $19, "AppleWorks Data Base File"
 - Apple II File Type Note $1A, "AppleWorks Word Processor File"
 - Apple II File Type Note $1B, "AppleWorks Spreadsheet File"

The file type notes have detailed information on all three formats, as defined by AppleWorks v3.0.

The AppleWorks v4 and v5 manuals have some useful information on updated features, but do not
document changes to the file formats.

## General ##

[AppleWorks](https://en.wikipedia.org/wiki/AppleWorks) is an integrated office suite, combining
a word processor, database, and spreadsheet into a single application.  It was developed by
Rupert Lissner for Apple Computer, initially released in 1984 for the Apple II.  Shortly after
its release, it became the best-selling software package on any computer, with more than a
million copies sold by the end of 1988.  Support for the software was handed off to Claris, an
Apple subsidiary.

An Apple /// version was developed, but before being released it was sold off to Haba Systems,
which sold it as "/// E-Z Pieces".  Both programs used the same file format.

A series of enhancements were published by Beagle Bros with the "TimeOut" name.  Version 3.0 of
AppleWorks was developed by Beagle Bros, on contract from Claris.

Claris eventually lost interest in Apple II products, and licensed AppleWorks to Quality Computers,
which released v4 and v5 in the early 1990s.

AppleWorks GS shares little but the name with AppleWorks.  AWGS started out as GS Works, developed
by StyleWare, and was renamed after being purchased by Claris.

Claris developed integrated office suites for the Macintosh and Windows, called ClarisWorks.  After
Claris was absorbed back into Apple, the programs were renamed to "AppleWorks".  The original
AppleWorks is sometimes called "AppleWorks Classic" to differentiate it from the Mac/Windows
products and AWGS.

## File Details ##

The three types of files have very different structures, but they all end with $ff $ff.  The
file type notes describe the file contents of v3.0 files in detail.

All multi-byte integers are stored in little-endian order.

### ProDOS Auxiliary Type ###

The 16-bit aux type is used to hold lower-case flags for the 15-character filename.  A '1' bit
indicates lower-case.  This has no effect for numbers, but a lower-case period ('.') is displayed
as a space (' ').

This is independent of the ProDOS filesystem convention, which was introduced with GS/OS.

### Tags ###

Version 3.0 formalized a tagged data extension, allowing arbitrary data to be appended to the
file.  Tags have the form:
```
+$00 / 1: must be $ff
+$01 / 1: tag ID, assigned by Beagle Bros
+$02 / 2: data length in bytes, up to 2048
+$04 /nn: data
```
The start of the next tag immediately follows the data from the previous tag.

The last entry is special: +$02 is a count of the tags in the file, +$03 is $ff, and there is
no following data.

It's unclear which programs used this feature, or what the assigned IDs are.

### Post-v3 Changes ###

v5 introduced support for inverse and MouseText characters.  These just use previously-unused
byte ranges.  The definition for character encoding becomes:
```
 $00-1f: special
 $20-7f: plain ASCII
 $80-9f: inverse upper case (map to $40-5f)
 $a0-bf: inverse symbols/numbers (map to $20-3f)
 $c0-df: MouseText
 $e0-ff: inverse lower case (map to $60-7f)
```
MouseText and inverse text available for use in Word Processor and Data Base files.


## Data Base Files ##

The basic file structure is:

 - Variable-length header.
 - Zero or more report records, 600 bytes each.  The original limit of 8 was increased to 20
   in v3.0.
 - Standard values record.
 - Zero or more variable-sized data records.
 - End marker ($ff $ff).
 - Tag data.

Each data record is a series of control bytes, which may be followed by data.  The structure
generally has one entry per category:
```
+$00 / 2: number of bytes in remainder of record
+$02 / 1: category #0 control byte
+$03 /nn: category #0 data
+$xx / 1: category #1 control byte
+$yy /nn: category #1 data
 [...]
```
The control byte may be:
```
 $01-7f: number of following bytes for this category
 $81-9e: number of categories to skip (value minus $80)
 $ff   : end of record
```
In the simplest case, the category data is a string, with the control byte providing the length.

There are special categories for dates and times, to allow them to be sorted correctly.  The first
byte of the category data will be $c0 for a date (high-ASCII '@'), and $d4 (high-ASCII 'T') for
time.  (It's unclear how this interacts with inverse/MouseText encoding in v5.  The AW5 delta
manual shows 4-digit years, so it's possible the date/time encoding system was revamped post-v3.0.)

Dates are stored in a 6-byte field, as `XYYMDD`, where X is $c0, Y and D are ASCII digits '0'-'9',
and M is a month code.  Month codes are 'A' for January, 'B' for February, and so on.  These are
displayed by AppleWorks in day-month-year format, e.g. "5 Jan 70".  If the year or day is `00`,
then that value is not specified (e.g. it's a list of people's birthdays without the birth year),
and the date should be displayed as "5 Jan" or "Jan 70".  This means that the year 2000 cannot be
represented directly.

Times are stored in a 4-byte field, as `XHMM`, where X is $d4, H is an hour code, and M are ASCII
digits '0'-'9' for the minutes.  The hour code is 'A' for midnight, 'B' for 01:00, and so on
through 'X' at 23:00.  AppleWorks displays times as 12-hour AM/PM.

The category-skip values are used to skip over entries that don't have data.  The control byte is
not followed by additional data for that category.  A skip value of 1 only skips the current
category.

## Word Processor Files ##

The basic file structure is:

 - 300-byte file header.
 - Zero or more variable-sized line records.
 - End marker ($ff $ff).
 - Tag data.

## Spreadsheet Files ##

The basic file structure is:

 - 300-byte file header.
 - Zero or more variable-length row records, each of which holds a sparse collection of cells.
 - End marker ($ff $ff).
 - Tag data.

Each row record is:
```
+$00 / 2: number of bytes in remainder of record
+$02 / 2: row number, starting with 1
+$04 /nn: variable-length data, containing a series of cell entries
```
Each cell record is variable-length, and starts with a byte full of bit flags that provide
information about the contents and presentation.  The contents of the rest of the cell entry
vary based on the type.

Column references are output as a letter, starting with "A" for column 0, "AA" for column 26, up
to "DW" for column 127.

Numeric values are stored as 64-bit floating point values, using the Standard Apple Numerics
Environment (SANE).  These are equivalent to IEEE754 values.
