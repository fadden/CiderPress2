# Editing Attributes

You can change a file's name, type, dates, access flags, and comment, and edit the
attributes of a volume directory.

---

## File Attributes

Select a file and choose **Actions → Rename / Edit Attributes…** (also on the
right-click menu). The dialog shows **only** the attributes that the current archive or
filesystem can actually store, so the exact set of fields varies by format.

### Filename

Every entry has a filename; in file archives it's often a partial pathname. The allowed
syntax and any restrictions are listed beneath the field.

> **CP/M user numbers** are treated as part of the filename, written after a comma and
> omitted for user 0. `FILE.TXT` is in user 0; `FILE.TXT,1` is the same name in user 1.
> Change the user assignment by editing this suffix.

### ProDOS type

Shown only when the format supports ProDOS types. Pick the file type from a numerically
sorted pop-up (the standard abbreviation is shown); enter the auxiliary type as a
4-digit hex value. If the type combination is recognized, a description of the expected
contents appears. On filesystems with a limited type set (DOS 3.3, Pascal), the list is
restricted accordingly. Directory entries' types can't be changed.

### HFS type

Shown only when the format supports HFS types. There are two fields each for type and
creator: a character representation on the left and a hex value on the right (e.g. type
`TEXT` ↔ `54455854`). Edit either field; clearing it sets the value to zero. Directory
entries' types can't be changed.

### Timestamp

Shown only when the format supports timestamps. Modification dates are widely
supported; creation dates less so. Enter dates and times in your **local timezone**,
24-hour format. Each format has a limited valid range (you can't enter dates outside
it); clearing the field sets "no date".

### Access

Shown only when the format supports access flags. Flags use ProDOS-style
access-*enabled* bits (Delete, reName, Write, Read, Backup, Invisible). Inapplicable
flags are disabled.

### Comment

Shown only when the format supports per-entry comments. Edit freely within the format's
length limit.

---

## Volume Attributes

To edit a volume directory, select the **top entry** in the Directory tree, then choose
**Actions → Edit Directory Attributes…** (or right-click the entry). This lets you
change the volume name and, where supported, some dates.

For DOS 3.2/3.3 this is where you change the volume number stored in the DOS VTOC. Note
that it does **not** change the sector numbers embedded in nibble images.

