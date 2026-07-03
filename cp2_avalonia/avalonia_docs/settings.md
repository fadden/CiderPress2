# Settings

Open the settings dialog with **Edit → Settings…**. It's organized into tabs; the
**General** tab holds the application-level preferences.

---

## General

### Auto-Open Depth

Controls how deeply CiderPress II scans an opened file for *other* archives nested
inside it:

- **Shallow**: open only the top level; don't scan for nested disks/archives.
- **Sub-Volume (recommended)**: scan for filesystem sub-volumes and for disk images
  inside archives, but don't open file archives or disk images stored inside other file
  archives or disk images (with some exceptions).
- **Max**: examine all files recursively. Thorough, but can take a few seconds on
  large collections.

### Theme

Choose the application appearance:

- **Light**
- **Dark**
- **System Default**: follow your operating system's appearance setting.

*(This is new in the cross-platform version; the old WPF build was light-only.)*

### Import/Export Conversion

The **Configure Import Options** and **Configure Export Options** buttons open the same
converter-configuration windows as the buttons in the main window's settings panel,
but here they're available even when no file is open. Use them to set defaults such as
the character set for text import or the color mode for Apple II hi-res export.

### Apple II Cassette Decoder

Selects the method used when decoding Apple II audio-cassette recordings. **Zero-Crossing
(recommended)** works for most files; the **Peak-to-Peak Width** variants (Sharp, Round,
Shallow) can help with distorted audio. This affects files as they're opened; it won't
re-analyze a file that's already open.

### Enable MacZip Handling

When enabled, files in a ZIP's `__MACOSX` directory (AppleDouble "header" files holding
attributes and resource forks) are hidden, and the corresponding entries are shown with
full attributes. When disabled, all files appear exactly as a standard ZIP utility would
show them.

### Enable DOS Text Conversion

When enabled, DOS text files copied **between a DOS filesystem and a different
filesystem** (via drag & drop or copy & paste) have the high bit set or cleared as
appropriate. It does not affect add/extract (which never modifies files) or import/export
(which always modifies files), and applies only to direct disk-to-disk copies. See
[DOS 3.x Considerations](drag-drop-clipboard.md#dos-3x-considerations).

### Dialog Messages

Some warning dialogs offer a "don't show this again" option. The **Reset suppressed
messages** button re-enables all of them. A label shows how many are currently
suppressed.

### Enable DEBUG menu

Adds a **DEBUG** menu with developer/diagnostic tools. Not generally useful; see
[Tools & Extras](tools.md#the-debug-menu).

---

## Where Settings Are Stored

Application settings live in a file named **`CiderPress2-settings.json`**, located
alongside the CiderPress II executable. Delete it to revert all settings to their
defaults. Window position and size are also saved here and restored on the next launch. 
