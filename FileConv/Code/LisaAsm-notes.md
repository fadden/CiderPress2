# Lazer's Interactive Symbolic Assembler (LISA) Source File #

File types:
 - v2: DOS alternate B
 - v3: ProDOS INT with aux type in range [$1000,$3fff]
 - v4/v5: ProDOS INT with aux type in range [$4000,$7fff]

Primary references:
 - v2: reverse engineering, primarily by Andy McFadden
 - v3+: LISA source code, from A2ROMulan CD-ROM

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

TODO

## v4/v5 Format ##

TODO
