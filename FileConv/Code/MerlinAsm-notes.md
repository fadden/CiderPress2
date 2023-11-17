# Merlin Assembler #

File types:
 - ProDOS TXT or DOS T, with filename extension ".S"

Primary references:
 - Reverse engineering

## General ##

[Merlin](https://en.wikipedia.org/wiki/Merlin_(assembler)) is an assembler written by Glen
Bredon for the Apple II.  Initially written for DOS 3.3, an updated version called "Merlin Pro"
was published after ProDOS was released.  After the Apple IIgs shipped, Merlin Pro was renamed
to "Merlin 8", and a IIgs-specific version called "Merlin 16" was released.

Merlin provided its own source code editor.  Source files use a customized text format.

## File Format ##

Assembly source files have four columns:

 1. label
 2. opcode
 3. operand
 4. comment

To save space in memory and on disk, the contents of each column are separated by a single
character.  Each column starts at a specific column on-screen.  Lines starting with '*'
are treated as full-line comments.

The Apple II screen was 80 columns wide, so the code was generally formatted to fit in that
space.  The default placement of the four columns positioned them at screen positions
1, 10, 16, and 27, respectively (where 1 is the left edge of the screen).

The file contents are generally high ASCII, but spaces in comments and quoted text in operands
are encoded as 0x20.  Columns are separated with a single space character (0xa0).  Encoding spaces
this way makes it easy to find the column breaks, because you don't have to worry about tracking
whether you're inside quoted text.  Lines end with a carriage return (0x8d).

## Similar Formats ##

The DOS Toolkit ED/ASM source format was very similar, but didn't use 0x20 for spaces in comments
and quoted operands.
