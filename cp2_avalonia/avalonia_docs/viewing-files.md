# Viewing Files

CiderPress II's **file viewer** lets you read documents and view graphics without
extracting them first, converting Apple II formats to modern equivalents on the fly.

Open it by **double-clicking** a file, by selecting one or more files and pressing
**Enter**, or via **Actions → View Files**. Use **Alt+Enter** (or **View Files in New
Viewer**) to open in a separate, additional window so you can compare files
side by side.

---

## Layout of the Viewer

```
+-------------------------------------------------------------------+
| [<-] [->] | [up] [down] | [*] [Aa] [0x] |     Find: [_____] [<][>]|
+----------------+--------------------------------------------------+
| Conversion:    |                                                  |
| [dropdown    ] |                                                  |
|                |                 file content                     |
| Options        |          (text, hex dump, or image)              |
| [ ... ]        |                                                  |
|                |                                                  |
| [Save as       |                                                  |
|  Default Cfg]  +--------------------------------------------------+
|                |          [ Data Fork ][ Resource Fork ][ Notes ] |
+----------------+--------------------------------------------------+
| [x] Open raw (DOS 3.x)            [Copy] [Export...] [Done]       |
+-------------------------------------------------------------------+
```

### Toolbar (top)

- **← / →**: Back / Forward through your viewing history. Right-click either button
  for a history list to jump directly.
- **↑ / ↓**: Move to the previous / next file in the current file list, so you can
  page through a folder without returning to the main window.
- **★ / Aa / 0x**: Quick conversion overrides:
  - **★** Use the *best* (automatic) conversion for this file.
  - **Aa** Force display as **plain text**.
  - **0x** Force a **hex dump**.
- **Find**: search for text within the displayed content (see below). For images,
  this area is replaced by **zoom** controls.

### Conversion panel (left)

- **Conversion** dropdown: pick how the file is interpreted. CiderPress II preselects
  the best match (an Applesoft program as a listing, a hi-res screen as an image, a
  text file as text, and so on), but you can override it.
- **Options**: converter-specific settings appear here when the chosen converter
  supports them (otherwise it reads "No conversion options available").
- **Save as Default Configuration**: remember the current converter and option
  choices as your default for files of this kind.

### Content tabs (bottom of the content area)

- **Data Fork**: the main file contents.
- **Resource Fork**: present for forked files; disabled when there's no resource
  fork.
- **Notes**: any warnings the converter or filesystem produced for this file.

Disabled tabs are dimmed; the active tab is emphasized.

### Bottom bar

- **Open raw (DOS 3.x only)**: for DOS 3.2/3.3 files, view the raw on-disk bytes
  rather than the cooked file contents. Enabled only where it applies.
- **Copy**: copy the converted text (or image) to the clipboard.
- **Export…**: save the converted result to a file (e.g. write the PNG of a hi-res
  image, or the plain-text rendering of a document).
- **Done**: close the viewer.

---

## Conversions

CiderPress II's conversion library understands a wide range of Apple II and Mac
formats and renders them to platform-neutral results that become **text, RTF, PNG, or
CSV** when copied or exported. Examples of what the viewer can show:

- Applesoft and Integer BASIC programs as readable listings.
- Apple II hi-res, double hi-res, and Super Hi-Res graphics as images.
- AppleWorks and other word-processor documents as formatted or plain text.
- Generic text with control-character handling, and a raw **hex dump** for anything
  else.

Because conversion happens in memory, you can switch converters and options and see
the result update immediately, then **Copy** or **Export** the version you want.

---

## Viewing Graphics

When the content is an image:

- The **zoom** controls (**−** / **+**, or the **`-`** and **`=`** keys) scale the
  image; the current factor is shown between them.
- The image renders with nearest-neighbor scaling so pixels stay crisp when enlarged.
- A checkerboard background indicates transparent areas.
- You can drag within the image to pan when it's larger than the view.
- You can also use Ctrl-Mouse Wheel to control the zoom in the image viewer.

---

## Searching Within a File

For text content, type in the **Find** box and use **Next** / **Prev** (or **F3** /
**Shift+F3**) to step through matches.

---

## Viewing Multiple Files

If you select several files before opening the viewer, the **↑ / ↓** buttons walk
through them. Combined with **Back / Forward** history, you can review an entire folder
quickly. Opening with **Alt+Enter** gives you an independent viewer window that stays
open alongside the main one.
