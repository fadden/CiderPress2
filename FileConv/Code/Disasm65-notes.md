# Notes on 65xx Disassembly #

File types:
 - 8-bit: BIN, REL, SYS, CMD, 8OB, P8C
 - 16-bit: OS
 - 16-bit, probably OMF: OBJ, LIB, S16, RTL, EXE, PIF, TIF, NDA, CDA, TOL, DVR, LDF, FST

Primary references:
 - _Programming the 65816, Including the 6502, 65C02, and 65802_, by David Eyes and Ron Lichty

## General ##

The original Apple II used the MOS 6502 CPU.  The 65C02 was intended to provide a superset of
functionality, and the 65816 is a superset of the 65C02.

In practice, the 6502 had a number of undocumented but useful opcodes.  In the Apple II world
these were rarely used, because writing code that wouldn't work on an enhanced Apple //e didn't
make sense.

Disassembling 6502 code is reasonably straightforward until code and data areas run together.
65816 code is harder, because the number of bytes used for certain opcodes can be different
based on the current register width setting.  The disassembler needs to provide options for
specifying the initial width of the registers, and make an effort to track changes to the
processor flags.

On the Apple IIgs, executable files are expected to be relocated when loaded, so disassembling
the code segments isn't generally all that successful.  Loading the OMF-format segments into
memory on a real or emulated IIgs, or into a virtual address area in the disassembler, yields
better results.  (The OMF loader tool in [SourceGen](https://6502bench.com/) can do this.)

The disassembler in CiderPress II decodes the instructions, and also formats and annotates
various operating system and IIgs toolbox calls.

## Nifty List Annotations ##

The annotations on memory locations and OS calls come from a file called NList.Data, which
was developed by David A. Lyons as part of his Nifty List desk accessory for the Apple IIgs.

The format of the Nifty List annotations may not be immediately obvious.  The following is an
excerpt from the Nifty List v3.4 manual.
```
------------------------------------
Interpreting the parameter summaries
------------------------------------
Some tools take no parameters and return no information.  These
appear with an empty pair of parentheses after the tool name:

   GrafOff()
   SystemTask()


Tools that take one or more parameters and return no information are
listed like this:

   SetPort(@Port)
   WriteBParam(Data,Parm#)

An "@" in front of a parameter means it is a pointer and takes 4
bytes (2 words).  All parameters not specially marked take 2 bytes
(1 word).


Tools that take no parameters but return one are listed like this:

   GetPort():@Port
   FreeMem():FreeBytes/4

A "/" and a digit after a parameter means it takes the specified number
of bytes.  (When making a tool call, you must push space on the stack
for any result values *before* pushing the input values.)


A few tools return more than one value.  In these cases, the
results are listed in the order they have been pushed onto the
stack (so that the first value PULLED is the last one listed):

   GetMouseClamp():Xmn,Xmx,Ymn,Ymx

Each of these values takes 2 bytes (1 word), since there is no
indication of a different size.


Tools that take and return values are listed like this, where a
trailing "H" indicates a Handle (4 bytes):

   EqualRgn(Rgn1H,Rgn2H):Flag

-----
Review of parameter sizes:

   Leading  "@"     4-byte pointer
   Trailing "H"     Handle (4 bytes)
   Trailing "/n"    n bytes

   All other values are 2 bytes long

-----
For ProDOS calls, the parameters are shown in parentheses even
though they actually belong in a parameter block.  For ProDOS 8,
the first item in the list is the parameter count, which should
be in the first byte of the parameter block.

   P8:RENAME(2:pn1,pn2)

ProDOS 16 calls (also called class-0 GS/OS calls) do not have a
parameter count.

   P16:CHANGE_PATH(@Path1,@Path2)

Class-1 GS/OS calls have parameter blocks beginning with a parameter
count word.  Some calls allow a range of values for the parameter
count (like Create, which can take from 1 to 7 parameters), and some
(like Destroy) have a single acceptable value:

   GS/OS:Create(1-7:@P,Acc,Typ,Aux/4,Stg,EOF/4,rEOF/4)
   GS/OS:Destroy(1:@P)
```
