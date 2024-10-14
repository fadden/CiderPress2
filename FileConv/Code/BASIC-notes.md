# Notes on BASIC Disassemblers #

This document has notes for:
 - [Applesoft BASIC](#applesoft-basic) for the Apple II
 - [Integer BASIC](#integer-basic) for the Apple II
 - [Business BASIC](#business-basic) for the Apple ///


## Applesoft BASIC ##

File types:
 - BAS ($fc) / any (aux type is often $0801)

Primary references:
 - Applesoft disassembly, by Bob Sander-Cederlof.  https://6502disassembly.com/a2-rom/

Applesoft BASIC, developed by Microsoft and enhanced by Apple, is one of the most popular
programming languages for the Apple II.

Applesoft programs are stored in tokenized form.  BASIC statements like "PRINT" are converted
to a single byte, while strings and numbers are stored as text.

### File Structure ###

The general structure of an Applesoft program is a series of lines.  Each line consists of:
```
  +$00 / 2: address of next line
  +$02 / 2: line number, 0-63999
  +$04 / N: variable number of bytes; mix of literals and tokens, ending with $00
```
At the end of the file, the line consists solely of an "address of next line" value of $0000.
(Some files appear to have one additional byte past this.  Might be because `NEW` sets $af-b0 to
$0804, while `FP` sets it to $0803?)

The address and line number are stored little-endian.  Byte values < 128 are literals, while
values >= 128 are tokens.

The tokenized form does not include whitespace, except in quoted text, REM statements, and DATA
statements.  Numeric values are stored as ASCII strings, and are converted to binary form every
time they are encountered.

Converting the bytecode form to "source" form is trivial, requiring only a simple conversion
of tokens to strings.  Converting text source to bytecode is slightly more complicated.  The
parser must find the longest match when parsing tokens, preferring "ONERR" over "ON", and must
recognize "?" as shorthand for "PRINT".  It also requires special handling for "AT" and "TO".
Whitespace is taken into account to differentiate "AT N" from "ATN" (arctangent) and
"A TO" from "AT O".

The program is stored on disk as it appears in memory.  Because it includes absolute addresses,
and the load address of the program isn't included in the file, the addresses stored in the file
are meaningless.  The OS file loader must re-write them by scanning through the program when
it's loaded.

### Misc ###

There are multiple versions of Applesoft.  The answers to
[this question](https://retrocomputing.stackexchange.com/q/384/56) provide a nice description
of the evolution.

The ROM-based version most people are familiar with loaded the program at $0801.  The byte in
memory immediately before the program ($0800) must be zero, or the program will not execute.  The
reason for this behavior is explained [here](https://retrocomputing.stackexchange.com/a/20180/56).


## Integer BASIC ##

File types:
 - INT ($fa) / any

Primary references:
 - Integer BASIC disassembly, by Paul R. Santa-Maria.  https://6502disassembly.com/a2-rom/
 - FID.C by Paul Schlyter

Integer BASIC was the first BASIC programming language shipped in the Apple II ROM.  Written by
Steve Wozniak, there is famously no source code, just a binder full of notes.

Integer BASIC programs are stored in tokenized form.  Statements like "PRINT" are converted
to a single byte, numbers are stored as 16-bit integers, and strings and variable names are
stored as text.

### File Structure ###

A file is a series of lines.  Each line is:
```
+$00 / 1: line length (including the length byte itself)
+$01 / 2: line number (must be positive)
+$03 /nn: series of variable-length bytecode values
+$xx / 1: end-of-line token ($01)
```
Integers are stored in little-endian order.  There is no end-of-file marker.

The bytecode stream values have the following meanings:
```
 $00   : invalid token in program
 $01   : end of line
 $02-11: invalid token in program
 $12-7f: language token
 $b0-b9: ('0'-'9') start of integer constant; followed by 16-bit value
 $ba-c0: (invalid)
 $c1-da: ('A'-'Z') start of a variable name; ends on value < $80 (i.e. a token)
 $db-ff: (invalid)
```
All byte values are possible, e.g. in integer constants.  The "invalid token" values may be
valid when typed directly on the command line, but not in the program itself.  For example,
you're not allowed to use `DEL` or `RUN` within a program.

In some cases, multiple tokens have the same name.  For example, there are separate tokens for
`RUN` with and without a line number (run from start vs. run at line).


## Business BASIC ##

File types:
 - BA3 ($09) / any (aux type usually $0000 or $0200)

Primary references:
 - Program lister, written by David Schmidt for original CiderPress.

Apple's Business BASIC ran on the Apple ///.  It offered a number of improvements over Apple II
BASIC.

### File Structure ###

All integers are stored in little-endian order.

The file structure is:
```
+$00 / 2: file length (does not include this length word)
+$02 /nn: zero or more lines
+$xx / 2: end-of-file marker ($0000)
```

Some files have a stored file length of zero, and may omit the end-of-file marker.

Each line is:
```
+$00 / 1: offset to next line
+$01 / 2: 16-bit line number
+$03 /nn: mix of tokens and literals
+$xx / 1: end-of-line token ($00)
```

Numbers are stored in character form, and are parsed during execution.

The end of the program is indicated by a line with a zero value for the offset to the next line.
