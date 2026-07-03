# Drag & Drop, Copy & Paste

CiderPress II lets you move files by dragging them or by using the clipboard. The same
settings that control [Add/Extract/Import/Export](add-extract-import-export.md) apply,
so the **Drag & Copy mode** toolbar selector decides whether a transfer behaves like
**Add/Extract** or **Import/Export**.

> **Cross-platform note.** Drag-and-drop behavior depends on your operating system's
> windowing support. Where a platform limits cross-application dragging, the
> **clipboard** (Copy/Paste) and the **menu commands** always work. Linux specifics
> are covered at the end of this page.

---

## Drag & Drop to Move Files (within CiderPress II)

Within the application, drag-and-drop **moves** files between directories on
hierarchical filesystems (ProDOS, HFS).

- All selected files are moved to the target directory, even if they came from
  several different folders. To keep a substructure intact, move a *directory* rather
  than its individual files.
- Unlike inter-application copies, this is **not** affected by Full List vs. Directory
  List view.
- Dragging a file into the directory it already lives in does nothing.
- You can drop onto the **file list** or the **Directory tree**. In the file list, if
  you drop on a directory entry, that becomes the destination; otherwise the directory
  currently selected in the Directory tree is used. **Dropping on the empty space below
  the list targets the volume (root) directory** of the current filesystem.
- File order within a directory is preserved as much as possible, following the order
  shown on screen.

> Copy & paste *within a file archive* isn't allowed; an entry can't be read while the
> archive is being rebuilt. Copy & paste within a disk image is fine, though copying a
> file onto itself fails.

---

## Copy & Paste / Drag Between Applications

You can copy or drag files from the file list to another program (your system file
manager, for example), and paste or drop files from other programs into CiderPress II.

- **Copy** (Ctrl/⌘+C) and outbound **drag** behave identically.
- **Paste** (Ctrl/⌘+V) drops into the directory currently selected in the Directory
  tree. A drop into the file list uses the dropped-on directory, or the current
  directory if you didn't drop on one.
- When transferring out, data and resource forks are arranged into separate files with
  names and contents appropriate for the host and the selected preservation scheme
  (NAPS, AppleSingle, etc.), so the receiving program can write them without further
  help.
- Transfers **between two CiderPress II instances** use a richer format that carries
  both forks and all attributes. Options like *strip paths* and *raw* change how the
  data is placed on the clipboard.

> Because file data is materialized at copy time, you generally can't copy from one
> image, close it, open another, and paste. Keep the source open until the paste
> completes.

If you copy a disk image or file archive that is itself open as a sub-volume in the
Archive Contents tree, it is closed automatically when copied.

---

## Dragging In From Your File Manager

Files dragged from your system file manager arrive as a list of filenames and are
handled like any other **add/import** operation (subject to the current Drag & Copy
mode and panel settings).

If **no archive is open**, dropping a file onto the main window makes CiderPress II try
to **open** it instead.

> One limitation carried over from the original: if your file manager is presenting a
> ZIP archive as if it were a folder and you drag files out of it, those come across as
> virtual streams, which isn't supported. Extract the ZIP first.

---

## Partial Pathname Handling

When copying or dragging *out*, the pathnames that result depend on the current view,
exactly like extraction:

- In **Directory List** view showing `Vacation`, copying `Snow.jpg` pastes it as
  `Snow.jpg`.
- In **Full List** view, the same file carries its path (`MyPhotos/Vacation/Snow.jpg`)
  and recreates that hierarchy at the destination.

This is independent of **Strip Paths**, which always removes *all* paths. The
single-directory behavior matches typical file-manager drag-and-drop, which is always
effectively showing one folder. Note that file archives are always shown in Full List
mode, so you can fully strip their paths but can't specify a partial hierarchy.

When copying between CiderPress II instances, the *receiving* side can also strip paths
(the option is set separately for add and extract).

---

## DOS 3.x Considerations

Two things matter when copying DOS files:

- **Raw mode**: the **Raw** option determines how DOS data is placed on the clipboard.
  Enable it for **DOS-to-DOS** transfers, disable it otherwise. You only need to set it
  on the *sending* side; the flag travels with the data.
- **Text conversion**: DOS text files set the high bit on every character; ProDOS and
  others clear it. With **Enable DOS Text Conversion** turned on (in Settings, on the
  *receiving* side), copies *between filesystems* (e.g. DOS→ProDOS, ProDOS→DOS) adjust
  the high bit automatically. It applies only to direct disk-to-disk copies, not
  add/extract (never modifies) or import/export (always converts), and not to
  transfers with other applications (use import/export text for those).

---

## Add/Extract vs. Import/Export for Drag & Copy

The **Drag & Copy mode** selector on the toolbar chooses the behavior:

- **Add/Extract**: faithful transfer (and the only mode that works **between
  CiderPress II instances**; in Import/Export mode the direct-transfer data isn't placed
  on the clipboard and pasting it into CiderPress II errors out).
- **Import/Export**: convert during transfer. If the export conversion is a specific
  type (not *Best*), only files suited to that conversion are exported.

> **Changing the mode clears any pending copied data.** Because files are prepared on
> the clipboard using the current mode's settings, switching modes discards a pending
> copy so nothing is pasted with the wrong format assumptions. Re-copy after switching.

---

## Linux Notes

On Linux, CiderPress II runs as an X11 client (including under XWayland on Wayland
desktops). Drag-and-drop and clipboard interop with desktop file managers (Dolphin,
Nautilus, Thunar, etc.) are handled through a built-in compatibility layer.

- **Clipboard Copy/Paste** works in both directions, including pasting files copied in
  a file manager into CiderPress II.
- **Internal drag-move** within the window works normally.
- Under **KDE Plasma on Wayland (XWayland)**, you may see an incorrect "no-drop" cursor
  glyph while dragging from CiderPress II. This is cosmetic only; the operation still
  completes correctly. It stems from a compositor limitation that a future native
  Wayland backend (planned for a later Avalonia release) will resolve.

If a drag operation ever seems not to take, fall back to **Copy/Paste** or the
**Actions** menu commands, which always work regardless of platform.

