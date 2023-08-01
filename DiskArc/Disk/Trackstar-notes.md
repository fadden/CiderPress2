# Trackstar APP Disk Image #

## Primary References ##

- Various reverse-engineering efforts
- Samples from https://www.diskman.com/presents/trackstar/

## General ##

The Trackstar series of cards, developed by Diamond Computer Systems, allowed an IBM PC-compatible
computer to work as an Apple II clone.  The cards allowed an Apple II 5.25" disk drive to be
connected directly, allowing access to copy-protected disks.

The "Trackstar E" model, which emulated an enhanced Apple IIe, provided a way to capture disk
images in "Trackstore" format.  These ".APP" files (short for Apple, not Application) could
hold whole tracks and half tracks.

Trackstar images are a slight improvement over unadorned nibble files (.nib), because they allow
tracks to be variable length.

## File Layout ##

A file holds 40 or 80 tracks.  Each track occupies $1a00 (6656) bytes.
```
+$0000 / 46: ASCII description of contents, padded with spaces; same on every track
+$002e / 82: zeroes
+$0080 /  1: $00 for 40-track image, $01 for 80-track image; same on every track
+$0081 /6525: nibble data buffer
+$19fe /  2: length of track data, or zero if analysis failed
```

The data starts at +$0081 and continues for up to (6656-129-2=6525) bytes.  The bytes past
the declared length should be ignored.  Unusually, the data is stored in descending order, so
a program that reads forward through the disk should read backward through memory.  (The "junk"
at the end is stored in ascending order, and is likely leftover data from the disk read that
wasn't zeroed out.  A quick sample showed that the bytes match those at the start of the track.)

The nibble data is whole bytes as read from the disk controller, so self-sync patterns are not
recoverable.

## Performance Note ##

The performance of the disk images on a physical Trackstar device seems to be affected by the
manner of their creation.  Brandon Cobb wrote (29-Sep-2018):

> [...] I noticed that disk images created by Ciderpress load
> *very, very, /very/ slowly* when used with the actual Trackstar. This, I
> think, is perhaps not caused by incomplete support however. Why do I
> think this? Well, it's because I've had to "fix" some of the 4am cracks
> to work on the Trackstar by copying the files to a standard DOS 3.3
> disk. If I do this on the Trackstar, using Trackstar format disk images,
> the "fixed" images load super slow too, just like the Ciderpress-created
> ones. So what do I have to do? *sigh* I have to create the "fixed" disk
> images in AppleWin, to the DSK format. And *then* convert those over to
> the custom Trackstar APP format, using the Trackstar.

This was explored a bit further in [this bug](https://github.com/fadden/ciderpress/issues/34).
The only interesting difference from CiderPress-generated images was which sector appeared
first in each track.
