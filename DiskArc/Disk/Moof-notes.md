# MOOF Disk Image Format #

## Primary References ##

- https://applesaucefdc.com/moof-reference/

## General ##

The MOOF format was developed by John K. Morris as a way to store the data coming from low-level
disk reading hardware, such as the [Applesauce](https://applesaucefdc.com/hardware/).  It's
essentially a minor modification of his WOZ v2.1 format that emphasizes support for the Macintosh
instead of the Apple II.

Differences from WOZ:
 - Some of the fields in the INFO chunk are different, or have different interpretations.
   For example, the Disk Type field drops 140KB GCR floppies and adds 1.4MB MFM disks.
 - The set of standard fields in the META chunk is different.  Fields whose meaning changed
   were given different names, e.g. `side_name` vs. `disk_name`, avoiding ambiguity.
 - The bit data may be MFM instead of GCR.

For additional commentary, please refer to the [WOZ notes](Woz-notes.md).
