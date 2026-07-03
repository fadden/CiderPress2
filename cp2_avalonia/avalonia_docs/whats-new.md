# What's New (for WPF users)

If you used the previous Windows-only desktop build (the WPF version), the workflow is
unchanged; the trees, file list, settings panel, and menus all work the way you
remember. This page summarizes what's different in the cross-platform Avalonia
application.

---

## Cross-Platform

The biggest change: CiderPress II now runs natively on **Windows, macOS, and Linux**
from a single codebase, built on the Avalonia UI toolkit.

- On **macOS**, the menus appear in the system menu bar at the top of the screen, and
  the standard ⌘-based shortcuts apply.
- On **Linux**, the app runs as an X11 client (including under XWayland). See the
  [Linux notes](drag-drop-clipboard.md#linux-notes) for drag-and-drop specifics.

---

## Themes

A new **Theme** setting (Edit → Settings → General) offers **Light**, **Dark**, and
**System Default**. The old build was light-only.

---

## Inline File Finder

**Edit → Find** (Ctrl/⌘+F) now opens a **Find panel** docked at the bottom of the left
side. Type a filename fragment and jump between matches with the **▶ / ◀** buttons (or
Enter / Shift+Enter). A **Current archive only** checkbox limits the search to the
active container instead of everything open. This is separate from the in-file text
search in the [file viewer](viewing-files.md).

---

## Sector Editor: CP/M Ordering Consolidated

The separate **Edit Blocks (CP/M)** menu item has been removed. Open the editor in block
mode and use the new **Block order** dropdown (ProDOS / CP/M) in the *Advanced* panel.
You can switch between the two orderings without closing the dialog. See
[Sector & Block Editing](sector-editing.md).

---

## File List Quality-of-Life

- **Sort order is preserved across refreshes.** Adding, deleting, renaming, or pasting
  no longer resets your chosen column sort.
- **Renaming a directory updates child paths immediately** in the file list, rather than
  showing stale pathnames until a manual refresh.
- **The Directory tree syncs with the file list.** In full-list view, selecting a file
  highlights the folder that contains it.

---

## Drag, Drop & Clipboard

- **Dropping on empty space** below the file list now targets the volume (root)
  directory of the current filesystem.
- **Pasting files from your system file manager** into CiderPress II is now supported,
  in addition to dragging them in.
- **Newly added sub-volumes open automatically.** When you add or paste a disk image or
  archive into a file archive, it's opened in the Archive Contents tree without needing a
  manual double-click.
- See the [Linux notes](drag-drop-clipboard.md#linux-notes) for platform-specific
  drag-and-drop behavior, including a cosmetic cursor caveat under KDE Wayland.

---

## Debug Log

The debug log viewer adds a **Copy to Clipboard** button alongside the existing **Save
to File**, making it easy to attach logs to a bug report.

---

## Window Placement

The window's position, size, and state are saved and restored across sessions (stored in
`CiderPress2-settings.json` next to the executable).

---

## Status & Notifications

Brief confirmations now appear as a **toast** near the bottom-center of the window and
fade on their own.

---

## In Progress

- A Linux **`.desktop`** launcher and packaging refinements are planned.

For the conceptual differences from the *original* (2003) CiderPress, see
[Overview & Concepts](overview.md#how-ciderpress-ii-differs-from-the-original-ciderpress).
