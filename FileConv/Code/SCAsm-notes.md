# S-C Assembler Source File #

File types:
 - DOS I

Primary references:
 - Reverse engineering, primarily by Paul Schlyter

## General ##

The [S-C Assembler](https://www.txbobsc.com/scsc/) was developed by Bob Sander-Cederlof and
first published in 1978.  The assembler was used to write programs for a newsletter called
[_Apple Assembly Line_](https://www.txbobsc.com/aal/index.html), which was published from
October 1980 through May 1988.

Files were stored with DOS file type 'I', potentially creating confusion with Integer BASIC.  The
filenames often started with "S.", but that's not something that can be relied upon.

S-C Macro Assembler v3.0, which featured ProDOS support, was developed but not released.

## File Format ##

Source files use a custom format with run-length compression.  Runs of spaces, which are common
in assembly source code, use a very compact encoding.

Files are a series of lines.  Each line is:
```
+$00 / 1: line length (includes line length byte and line number)
+$01 / 2: line number
+$03 /nn: series of variable-length bytecode values
+$xx / 1: end-of-line token ($00)
```
The bytes in the line may have the following values:
```
 $00-1f: invalid
 $20-7f: literal character
 $80-bf: compressed spaces (value is count, 0-63 +$80)
 $c0   : RLE delimiter; followed by a count byte, then the value to repeat
 $c1-ff: invalid
```

There is no end-of-file marker.
