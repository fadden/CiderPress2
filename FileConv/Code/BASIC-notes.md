# Notes on BASIC Disassemblers #

This document has notes for:
 - [Integer BASIC](#integer-basic) for the Apple II
 - [Applesoft BASIC](#applesoft-basic) for the Apple II
 - [Business BASIC](#business-basic) for the Apple ///


## Integer BASIC ##

Primary references:
 - Integer BASIC disassembly, by Paul R. Santa-Maria.  https://6502disassembly.com/a2-rom/

[ TODO ]


## Applesoft BASIC ##

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


## Business BASIC ##

[ TODO ]
