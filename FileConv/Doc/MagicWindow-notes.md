# Magic Window Document #

File types:
 - DOS 'B' with filename extension ".MW"

Primary references:
 - Reverse engineering

## General ##

[Magic Window](http://www.artscipub.com/history/magicwindow/) was a text-based word processor
published by Artsci.  It featured a window that scrolled horizontally and vertically to
provide a WYSIWYG editor on a 40-column screen (assuming 10 CPI monospace output).

Documents are standard DOS 'B' files with a mystery value in the load address field.  They have
a 256-byte header, followed by high-ASCII text with occasional control codes for bold, italic,
and so on.  Line endings are indicated with a carriage return.  A ".MW" filename suffix is
enforced by the program.

An updated version of Magic Window was published for the Apple IIe.  The file format does not
appear to be significantly different, although a text area in the file header switched from
high ASCII to standard ASCII.

## File Format ##

The contents of the 256-byte header are not well understood.  In the original MW, the layout
looks like this:
```
+$00 / 1: $8d
+$01 / 1: unknown
+$02 /64: ASCII text, often just spaces, possibly document header and footer; high ASCII
+$52 /80: unknown, all $00?
+$a2 /94: unknown, generally nonzero values
```
The header changed a bit in Magic Window II:
```
+$00 / 1: $8d
+$01 / 1: unknown
+$02 /64: ASCII text, often just spaces, possibly document header and footer; low ASCII (DCI?)
+$41 / 1: $a0
+$42 /96: ASCII text, always $20?
+$a2 /94: unknown, generally nonzero values
```
See e.g. "CHAR.TABLE.MW" on the Magic Window II disk for an example with header text.

Documents may contain formatting control codes.  The list of codes provided by the program are
shown in "COMMAND CHART.MW":
```
Printer Tokens (^B codes):

Direct:
pica pitch (10 cpi)                       ^N
elite pitch (12 cpi)                      ^S
condensed pitch (17 cpi)                  ^T

Toggle on/off:
italics (alternate font) not IW           ^F
proportional                              ^P
underline on/off                          ^^
boldface (emphasized)                     ^]
expanded (double width)                   ^X
enhanced (double strike) not IW           ^Z
superscript                               ^A
subscript                                 ^B
```
For example, the first line in COMMAND CHART is underlined (Ctrl+^ is 0x1e):
```
000100: a0 a0 a0 a0 a0 a0 a0 a0 a0 a0 a0 a0 a0 a0 a0 a0                  
000110: a0 a0 a0 9e cd e1 e7 e9 e3 a0 d7 e9 ee e4 ef f7     ·Magic Window
000120: a0 c9 c9 a0 c3 ef ed ed e1 ee e4 f3 9e 8d 8d 8d   II Commands····
```
Note that centering was done manually with spaces.

It's possible to enter printer-specific escape codes directly; see "GEMINI CODES.MW" on the
Magic Window II disk for an example.
