# CFFA #

## Primary References ##

 - https://dreher.net/?s=projects/CFforAppleII&c=projects/CFforAppleII/main.php
 - Original CFFA manual:
   https://dreher.net/projects/CFforAppleII/downloads/CFforAppleII_Manual_1.0.pdf

## General ##

Initially developed by Rich Dreher in 2002, the CFFA card provided a way to use a Compact Flash
card as a hard drive on an Apple II.  The storage was divided into either 4 or 8 32MB volumes,
depending on how a jumper on the card was set.

A GS/OS driver by Dave Lyons allowed one or two additional drives at the end when in 4-volume mode.
The 5th volume immediately follows the 4th, and may be up to 1GB.  The 6th volume begins 1GB
after the start of the 5th, and may also be up to 1GB in size.

Any physical storage that lies past the last partition is wasted.

Files in this format are a little difficult to deal with because there is no partition map.  The
volumes simply follow one another, so recognizing a file as CFFA storage requires probing every
32MB to see what we find.  If one of the volumes is blank or has a filesystem we don't
recognize, we can't be entirely confident that what we've found is CFFA.  Further, if a card is
formatted in one way, and then reformatted in a different way without zeroing all the blocks, we
might see the previous layout and try to use that.

Because this storage originates on physical media, and physical media is not necessarily an exact
multiple of 32MB, the last volume is allowed to be smaller if it fills up the physical storage.

Only ProDOS and HFS filesystems are expected.  The maximum size of a ProDOS volume is 65535 blocks,
one block shy of 32MB, so a block of padding between volumes is expected.
