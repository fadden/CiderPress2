# Apple II Audio Cassette Recording #

## Primary References ##

 - General info: https://retrocomputing.stackexchange.com/a/144/56
 - Apple II ROM tape routines (READ, WRITE)

## Background ##

The typical storage medium used on early Apple II systems, before floppy
drives became commonplace, was a consumer-grade cassette tape recorder.  The
Apple II records data as a frequency-modulated sine wave.  A recording device
can be connected to the dedicated audio I/O port on the Apple ][, ][+,
and //e.  The //c, ///, and IIgs do not have this port.

A tape can hold one or more chunks of data, each of which has the following
structure:

 - Entry tone: 10.6 seconds of 770Hz (8192 cycles at 1300 usec/cycle). This
   lets the human operator know that the start of data had been found.
 - Tape-in edge: 1/2 cycle at 400 usec/cycle, followed by 1/2 cycle at
   500 usec/cycle. This "short zero" indicates the transition between
   header and data.
 - Data: one cycle per bit, using 500 usec/cycle for 0 and
   1000 usec/cycle for 1.

There is no "end of data" indication, so it's up to the user to specify the
length of data. The last byte of data is followed by an XOR checksum,
initialized to $FF, that can be used to check for success.

Typical instructions for loading data from tape look like this:

 - Type `LOAD` or `xxxx.xxxxR`, but don't hit &lt;return&gt;.
 - Play tape until you here the tone.
 - Immediately hit stop.
 - Plug the cable from the Apple II into the tape player.
 - Hit "play" on the recorder, then immediately hit &lt;return&gt;.
 - When the Apple II beeps, it's done.  Stop the tape.

The cassette I/O routines are built into the ROM.  For binary code or data,
the command is issued from the system monitor.  The length is specified on
the monitor command line, e.g. `800.1FFFR` would read $1800 (6144) bytes. For
BASIC programs and data, the length is included in an initial header section:

 - Integer BASIC programs have a two-byte (little-endian) length.
 - Applesoft BASIC programs (written with `SAVE`, loaded with `LOAD`) have a
   three byte header: the two-byte length, followed by a "lock" flag byte
   (if the high bit is set, the program starts executing when the `LOAD`
   completes).
 - Applesoft shape tables (loaded with `SHLOAD`) have a two-byte length.
 - Applesoft arrays (written with `STORE`, loaded with `RECALL`) have
   a three-byte header: the length, followed by an unused byte.

The header section is a full data area, complete with 10.6-second lead-in.

The storage density varies from 2000bps for a file full of 0 bits to 1000bps
for a file full of 1 bits. Assuming an equal distribution of bits, you can
expect to transfer about 187 bytes/second (ignoring the header).

## Signal Processing ##

Some notes on what's required to decipher the recorded signal...

The monitor ROM routine uses a detection threshold of 700 usec to tell
the difference between 0s and 1s.  When reading, it *outputs* a tone for
3.5 seconds before listening.  It doesn't try to detect the 770Hz tone,
just waits for something under (40*12=)440 usec.

The Apple II hardware changes the high bit read from $c060 every time it
detects a zero-crossing on the cassette input.  I assume the polarity
of the input signal is reflected by the polarity of the high bit, but
I'm not sure, and in the end it doesn't really matter.

How quickly do we need to sample?  The highest frequency we expect to
find is 2KHz, so anything over 4KHz should be sufficient.  However, we
need to be able to resolve the time between zero transitions to some
reasonable resolution.  We need to tell the difference between a 650usec
half-cycle and a 200usec half-cycle for the start, and 250/500usec for
the data section.  Our measurements can comfortably be off by 200 usec
with no ill effects on the lead-in, assuming a perfect signal.  (Sampling
every 200 usec would be 5Hz.)  The data itself needs to be +/- 125usec
for half-cycles, though we can get a little sloppier if we average the
error out by combining half-cycles.

The signal is less than perfect, sometimes far less, so we need better
sampling to avoid magnifying distortions in the signal.  If we sample
at 22.05KHz, we could see a 650usec gap as 590, 635, or 680, depending
on when we sample and where we think the peaks lie.  We're off by 15usec
before we even start.  We can reasonably expect to be off +/- twice the
"usecPerSample" value.  At 8KHz, that's +/- 250usec, which isn't
acceptable.  At 11KHz we're at +/- 191usec, which is scraping along.

We can get mitigate some problems by doing an interpolation of the
two points nearest the zero-crossing, which should give us a more
accurate fix on the zero point than simply choosing the closest point.
This does potentially increase our risk of errors due to noise spikes at
points near the zero.  Since we're reading from cassette, any noise spikes
are likely to be pretty wide, so averaging the data or interpolating
across multiple points isn't likely to help us.

Some tapes seem to have a low-frequency distortion that amounts to a DC
bias when examining a single sample.  Timing the gaps between zero
crossings is therefore not sufficient unless we also correct for the
local DC bias.  In some cases the recorder or media was unable to
respond quickly enough, and as a result 0s have less amplitude
than 1s.  This throws off some simple correction schemes.

The easiest approach is to figure out where one cycle starts and stops, and
use the timing of the full cycle.  This gets a little ugly because the
original output was a square wave, so there's a bit of ringing in the
peaks, especially the 1s.  Of course, we have to look at half-cycles
initially, because we need to identify the first "short 0" part.  Once
we have that, we can use full cycles, which distributes any error over
a larger set of samples.

In some cases the positive half-cycle is longer than the negative
half-cycle (e.g. reliably 33 samples vs. 29 samples at 48KHz, when
31.2 is expected for 650us).  Slight variations can lead to even
greater distortion, even though the timing for the full signal is
within tolerances.  This means we need to accumulate the timing for
a full cycle before making an evaluation, though we still need to
examine the half-cycle timing during the lead-in to catch the "short 0".

Because of these distortions, 8-bit 8KHz audio is probably not a good
idea.  16-bit 22.05KHz sampling is a better choice for tapes that have
been sitting around for 25-30 years.

## ROM Implementation ##

For reference, here is a disassembly of the ROM routines.  The code has been
rearranged to make it easier to read.

```
; Increment 16-bit value at 0x3c (A1) and compare it to 16-bit value at
;  0x3e (A2). Returns with carry set if A1 >= A2.
; Requires 26 cycles in common case, 30 cycles in rare case.
FCBA: A5 3C     709  NXTA1    LDA   A1L        ;INCR 2-BYTE A1.
FCBC: C5 3E     710           CMP   A2L
FCBE: A5 3D     711           LDA   A1H        ;  AND COMPARE TO A2
FCC0: E5 3F     712           SBC   A2H
FCC2: E6 3C     713           INC   A1L        ;  (CARRY SET IF >=)
FCC4: D0 02     714           BNE   RTS4B
FCC6: E6 3D     715           INC   A1H
FCC8: 60        716  RTS4B    RTS

; Write data from location in A1L up to location in A2L.
FECD: A9 40     975  WRITE    LDA   #$40
FECF: 20 C9 FC  976           JSR   HEADR      ;WRITE 10-SEC HEADER
; Write loop.  Continue until A1 reaches A2.
FED2: A0 27     977           LDY   #$27
FED4: A2 00     978  WR1      LDX   #$00
FED6: 41 3C     979           EOR   (A1L,X)
FED8: 48        980           PHA
FED9: A1 3C     981           LDA   (A1L,X)
FEDB: 20 ED FE  982           JSR   WRBYTE
FEDE: 20 BA FC  983           JSR   NXTA1
FEE1: A0 1D     984           LDY   #$1D
FEE3: 68        985           PLA
FEE4: 90 EE     986           BCC   WR1
; Write checksum byte, then beep the speaker.
FEE6: A0 22     987           LDY   #$22
FEE8: 20 ED FE  988           JSR   WRBYTE
FEEB: F0 4D     989           BEQ   BELL

; Write one byte (8 bits, or 16 half-cycles).
; On exit, Z-flag is set.
FEED: A2 10     990  WRBYTE   LDX   #$10
FEEF: 0A        991  WRBYT2   ASL
FEF0: 20 D6 FC  992           JSR   WRBIT
FEF3: D0 FA     993           BNE   WRBYT2
FEF5: 60        994           RTS

; Write tape header.  Called by WRITE with A=$40, READ with A=$16.
; On exit, A holds $FF.
; First time through, X is undefined, so we may get slightly less than
;  A*256 half-cycles (i.e. A*255 + X).  If the carry is clear on entry,
;  the first ADC will subtract two (yielding A*254+X), and the first X
;  cycles will be "long 0s" instead of "long 1s".  Doesn't really matter.
FCC9: A0 4B     717  HEADR    LDY   #$4B       ;WRITE A*256 'LONG 1'
FCCB: 20 DB FC  718           JSR   ZERDLY     ;  HALF CYCLES
FCCE: D0 F9     719           BNE   HEADR      ;  (650 USEC EACH)
FCD0: 69 FE     720           ADC   #$FE
FCD2: B0 F5     721           BCS   HEADR      ;THEN A 'SHORT 0'
; Fall through to write bit.  Note carry is clear, so we'll use the zero
;  delay.  We've initialized Y to $21 instead of $32 to get a short '0'
;  (165usec) for the first half and a normal '0' for the second half;
FCD4: A0 21     722           LDY   #$21       ;  (400 USEC)
; Write one bit.  Called from WRITE with Y=$27.
FCD6: 20 DB FC  723  WRBIT    JSR   ZERDLY     ;WRITE TWO HALF CYCLES
FCD9: C8        724           INY              ;  OF 250 USEC ('0')
FCDA: C8        725           INY              ;  OR 500 USEC ('0')
; Delay for '0'.  X typically holds a bit count or half-cycle count.
; Y holds delay period in 5-usec increments:
;   (carry clear) $21=165us  $27=195us  $2C=220 $4B=375us
;   (carry set) $21=165+250=415us  $27=195+250=445us  $4B=375+250=625us
;   Remember that TOTAL delay, with all other instructions, must equal target
; On exit, Y=$2C, Z-flag is set if X decremented to zero.  The 2C in Y
;  is for WRBYTE, which is in a tight loop and doesn't need much padding.
FCDB: 88        726  ZERDLY   DEY
FCDC: D0 FD     727           BNE   ZERDLY
FCDE: 90 05     728           BCC   WRTAPE     ;Y IS COUNT FOR
; Additional delay for '1' (always 250us).
FCE0: A0 32     729           LDY   #$32       ;  TIMING LOOP
FCE2: 88        730  ONEDLY   DEY
FCE3: D0 FD     731           BNE   ONEDLY
; Write a transition to the tape.
FCE5: AC 20 C0  732  WRTAPE   LDY   TAPEOUT
FCE8: A0 2C     733           LDY   #$2C
FCEA: CA        734           DEX
FCEB: 60        735           RTS

; Read data from location in A1L up to location in A2L.
FEFD: 20 FA FC  999  READ     JSR   RD2BIT     ;FIND TAPEIN EDGE
FF00: A9 16     1000          LDA   #$16
FF02: 20 C9 FC  1001          JSR   HEADR      ;DELAY 3.5 SECONDS
FF05: 85 2E     1002          STA   CHKSUM     ;INIT CHKSUM=$FF
FF07: 20 FA FC  1003          JSR   RD2BIT     ;FIND TAPEIN EDGE
; Loop, waiting for edge.  11 cycles/iteration, plus 432+14 = 457usec.
FF0A: A0 24     1004 RD2      LDY   #$24       ;LOOK FOR SYNC BIT
FF0C: 20 FD FC  1005          JSR   RDBIT      ;  (SHORT 0)
FF0F: B0 F9     1006          BCS   RD2        ;  LOOP UNTIL FOUND
; Timing of next transition, a normal '0' half-cycle, doesn't matter.
FF11: 20 FD FC  1007          JSR   RDBIT      ;SKIP SECOND SYNC H-CYCLE
; Main byte read loop.  Continue until A1 reaches A2.
FF14: A0 3B     1008          LDY   #$3B       ;INDEX FOR 0/1 TEST
FF16: 20 EC FC  1009 RD3      JSR   RDBYTE     ;READ A BYTE
FF19: 81 3C     1010          STA   (A1L,X)    ;STORE AT (A1)
FF1B: 45 2E     1011          EOR   CHKSUM
FF1D: 85 2E     1012          STA   CHKSUM     ;UPDATE RUNNING CHKSUM
FF1F: 20 BA FC  1013          JSR   NXTA1      ;INC A1, COMPARE TO A2
FF22: A0 35     1014          LDY   #$35       ;COMPENSATE 0/1 INDEX
FF24: 90 F0     1015          BCC   RD3        ;LOOP UNTIL DONE
; Read checksum byte and check it.
FF26: 20 EC FC  1016          JSR   RDBYTE     ;READ CHKSUM BYTE
FF29: C5 2E     1017          CMP   CHKSUM
FF2B: F0 0D     1018          BEQ   BELL       ;GOOD, SOUND BELL AND RETURN

; Print "ERR", beep speaker.
FF2D: A9 C5     1019 PRERR    LDA   #$C5
FF2F: 20 ED FD  1020          JSR   COUT       ;PRINT "ERR", THEN BELL
FF32: A9 D2     1021          LDA   #$D2
FF34: 20 ED FD  1022          JSR   COUT
FF37: 20 ED FD  1023          JSR   COUT
FF3A: A9 87     1024 BELL     LDA   #$87       ;OUTPUT BELL AND RETURN
FF3C: 4C ED FD  1025          JMP   COUT

; Read a byte from the tape.  Y is $3B on first call, $35 on subsequent
;  calls.  The bits are shifted left, meaning that the high bit is read
;  first.
FCEC: A2 08     736  RDBYTE   LDX   #$08       ;8 BITS TO READ
FCEE: 48        737  RDBYT2   PHA              ;READ TWO TRANSITIONS
FCEF: 20 FA FC  738           JSR   RD2BIT     ;  (FIND EDGE)
FCF2: 68        739           PLA
FCF3: 2A        740           ROL              ;NEXT BIT
FCF4: A0 3A     741           LDY   #$3A       ;COUNT FOR SAMPLES
FCF6: CA        742           DEX
FCF7: D0 F5     743           BNE   RDBYT2
FCF9: 60        744           RTS

; Read two bits from the tape.
FCFA: 20 FD FC  745  RD2BIT   JSR   RDBIT
; Read one bit from the tape.  On entry, Y is the expected transition time:
;   $3A=696usec  $35=636usec  $24=432usec
; Returns with the carry set if the transition time exceeds the Y value.
FCFD: 88        746  RDBIT    DEY              ;DECR Y UNTIL
FCFE: AD 60 C0  747           LDA   TAPEIN     ; TAPE TRANSITION
FD01: 45 2F     748           EOR   LASTIN
FD03: 10 F8     749           BPL   RDBIT
; the above loop takes 12 usec per iteration, what follows takes 14.
FD05: 45 2F     750           EOR   LASTIN
FD07: 85 2F     751           STA   LASTIN
FD09: C0 80     752           CPY   #$80       ;SET CARRY ON Y
FD0B: 60        753           RTS
```
