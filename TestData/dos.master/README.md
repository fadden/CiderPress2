Glen Bredon's DOS MASTER can embed multiple DOS volumes in a ProDOS
filesystem.  It has been released into the public domain.

Version 1.8 can be found on the Internet, but the only disk images I could
find were 32MB hard drive images or ShrinkIt file archives.  The original
distribution disk was a 140KB DOS/ProDOS hybrid that booted into ProDOS,
but reserved five tracks in the middle of the disk (17-21) for DOS use.
The "FUD" utility and an Integer BASIC loader were available there for access
from unmodified DOS 3.3.  The disk image here is for the original distribution
of DOS MASTER v1.7.  (It's not recognized as a DOS/ProDOS hybrid because the
catalog track is only 1 sector long.)

The other disks here are 800KB ProDOS images with various arrangements
of DOS volume sizes and counts, created by DOS.MASTER v1.7 in an emulator.
The "+" disks have volumes that cover the entire 800KB, but reserve the
first 8 tracks of the first DOS volume to allow the disk to be booted
through ProDOS into DOS.MASTER.  All disks have a BASIC program called
"HELLO" in the first partition, and a program called "HELLOn" in
partitions 2 and above.
