# Apple "Macintosh File System" (MFS) #

## Primary Sources ##

 - _Inside Macintosh, Volume II (1985)_ -- chapter 4, esp. page II-119+ describes the structure
   of MFS.
 - _Inside Macintosh, Volume IV_ (1986) -- page IV-89+ has some notes about 64K ROM features.
   Apparently the 128K ROM is when HFS was introduced.  It also talks about version numbers on
   files (and their lack of usefulness).  Page IV-160+ describes the MFS structure (appears to
   be a repeat of the Volume II chapter).
 - page IV-105 indicates that FInfo was created for MFS-era Finder, while FXInfo was added
   for HFS-era.
 - https://developer.apple.com/library/archive/samplecode/MFSLives/Introduction/Intro.html

## General ##

MFS was introduced with the original Macintosh in 1984.  It was replaced with HFS in 1986, with
the launch of the Macintosh Plus.  Support for MFS was included in the operating system until
Mac OS 8.1.

The volume directory was stored in a fixed-size area, but the size of each entry was variable.
Filenames could be up to 255 characters long.  File entries were not allowed to cross block
boundaries.

[ TODO ]
