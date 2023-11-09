# Gutenberg / Gutenberg Jr. Word Processor Format #

File types:
 - Text document on a disk with the Gutenberg filesystem
 - TODO: how do we tell Gutenberg and Gutenberg, Jr. apart?

Primary references:
 - Gutenberg tutorials (on distribution disks)
 - Gutenberg Jr. manual (on distribution disks)

## General ##

Gutenberg and Gutenberg, Jr. are fancy document creation systems that used a custom disk format.
See the [Gutenberg filesystem notes](../../DiskArc/FS/Gutenberg-notes.md) for more information
about the file structure.

## File Structure ##

Documents use a compact inline formatting system.

[ TODO ]

The "FONTS.TABLE" file on the Gutenberg, Jr. boot disk allows you to view all "standard" and
"alternate" characters.  The table does not have entries for $00, which is used to indicate
end-of-file, or $1f.  The reason for omitting the latter is unclear.
