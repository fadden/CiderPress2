# Trackstar APP Disk Image #

## Primary References ##

- Various reverse-engineering efforts
- Samples from https://www.diskman.com/presents/trackstar/
- Trackstar Plus manual

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

A file holds 40 or 80 tracks.  Each track occupies $1a00 (6656) bytes:
```
+$0000 / 46: ASCII description of contents, padded with spaces; same on every track
+$002e / 82: zeroes
+$0080 /  1: $00 for 40-track image, $01 for 80-track image; same on every track
+$0081 /6525: nibble data buffer
+$19fe /  2: length of track data, or zero if analysis failed
```

The data starts at +$0081 and continues for up to (6656-129-2=6525) bytes.  The bytes past
the declared length should be ignored.  Unusually, the data is stored in descending order, so
a program that reads forward through the disk should read backward through memory.

(The "junk" at the end is stored in ascending order, and is likely leftover data from the disk
read that wasn't zeroed out.  A quick examination of a couple of disks showed that the bytes that
follow are the palindrome of the bytes that immediately precede it.)

The nibble data is stored as 8-bit bytes as they were read from the disk controller, so extended
bytes in self-sync patterns are not identifiable.

40-track images include the whole tracks, 80-track images include both whole and half tracks.  The
purpose of 80-track images was to improve compatibility with copy-protected software.

### Counting Tracks ###

The "Trackstore" format always stores 40 or 80 tracks, even though most Apple II floppies only
use 35 tracks (because many drives can't reliably seek beyond that).  So what's in the
leftover tracks?

An analysis of a few images determined that, on a 40-track image of a 35-track disk, tracks 35-39
are repeated reads of track 34.  This can be seen by examining the sector headers, which
include the track number.  This is likely the result of capturing the disk images on a 5.25"
drive that had a hard stop at track 35: the software requested a higher-numbered track, but the
drive couldn't do it, so it re-read track 34 instead.

Other disk images have garbage for those tracks, with a track length of zero, indicating that
the Trackstore software was unable to recognize valid data.

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
