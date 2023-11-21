# Lazer's Interactive Symbolic Assembler (LISA) Source File #

File types:
 - v2: DOS alternate B
 - v3: ProDOS INT with aux type in range [$1000,$3fff]
 - v4/v5: ProDOS INT with aux type in range [$4000,$5fff]

Primary references:
 - v2: reverse engineering, primarily by Andy McFadden
 - v3: LISA v3.1 source code, from A2ROMulan CD-ROM
 - v4/v5: Lisa816 v5.0a (433) source code, from A2ROMulan CD-ROM

## General ##

[LISA](https://en.wikipedia.org/wiki/Lazer%27s_Interactive_Symbolic_Assembler) is an assembler
for Apple II code written by Randall Hyde.  The name is pronounced "LE ZA" rather than "LI SA".

The program kept the source file in tokenized form, which reduced the memory footprint and
the time required to assemble code.  This is true for all versions, even though the file format
changed significantly over time.

Version 3.x was the last for 8-bit computers.  Versions 4 and 5 were branded "LISA 8/16", and
added support for the Apple IIgs.

## v2 Format ##

The file structure is fairly straightforward:
```
+$00 / 2: program version? - usually $1800, which might indicate v2.4 (2.4 * 2560)
+$02 / 2: length of file data; needed because DOS 3.3 doesn't store this for alternate B
+$04 /nn: series of lines
```
Each line is:
```
+$00 / 1: line length (does not include length byte)
+$01 /nn: data
+$xx / 1: end-of-line marker ($0d)
```
The last line in the file has a length of 255 ($ff).  The last length byte is followed by two
more $ff bytes.

The line data is a mix of plain text and tokenized bytes.

Lines starting with '*' or ';' are full-line comments.  Lines starting with an upper-case letter
are code lines that start with a label, lines starting with a caret (^) have a local (numeric)
label, and lines starting with a space do not have a label.  All other characters are invalid at
the start of a line.  If present, regular labels always take 8 bytes in the source file, and
local labels take 7 bytes, padded with trailing spaces if necessary.  If the label is terminated
with a colon, it will be included at the end.

If the label is on a line by itself, the next character will be the end-of-line marker.  If not,
the next byte will be the encoded opcode or pseudo-op mnemonic, with a value >= 0x80.

The opcode is followed by a byte that indicates the contents of the operand field.  It will be $00
if there is no operand, $01 for absolute addressing, $02 for immediate, $05 for direct page
indirect indexed Y), and so on. Operands that should be taken literally, e.g.
`ASC "HELLO, WORLD!"`, use $20.  The text of the operand follows.

If the line has a comment, the operand will be followed by $bb (high-ASCII semicolon).  Because
this cannot appear in a valid operand, it's not necessary to track open/close quotes when
converting lines to text.  Lines without comments will follow the operand with $0d.

## v3 Format ##

Auxtypes $17fc, $1800, $1ffc, $2000, $26fc, and $27fc have been seen.

The file format looks like this:
```
+$00 / 2: length of code section, in bytes
+$02 / 2: length of symbol table, in bytes
+$04 /nn: symbol table, 0-512 entries, 8 bytes each
+$xx /yy: code
```
Symbol table entries are 8 bytes each:
```
+$00 / 6: 8-character label, packed into 6 bytes
+$06 / 2: value? (usually zero)
```
The code section is a series of lines with a complex encoding.

At least one file has been found that appears to use a different table of opcode mnemonics.  The
file "anix.equates" appears in some ANIX distributions, and has the same filetype and auxtype as
other source files.  However, even though it appears to decode successfully, all of the opcodes
are incorrect.  Curiously, the ANIX 2.1 command "LPRINT" can successfully convert "anix.equates"
to text, but generates incorrect output for LISA v3.1 source files.

## v4/v5 Format ##

Auxtypes $40e8, $40e9, and $50e1 have been seen.  A filename suffix of ".A" is commonly used.

The file format looks like this:
```
+$00 / 2: version number
+$02 / 2: offset of first byte past end of symbol table
+$04 / 2: symbol count
+$06 / 1: tab position for opcodes
+$07 / 1: tab position for operands
+$08 / 1: tab position for comments
+$09 / 1: CPU type
+$0a / 6: (reserved)
+$10 /nn: symbol table
+$xx /yy: code
```
The symbol table is a series of strings that have a preceding length and are null terminated.  The
length byte includes the null termination and the length byte itself.

The code section is a series of lines with a complex encoding.
