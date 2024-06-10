# Apple IIgs Teach Document #

File types:
 - GWP ($50) / $5445

Primary references:
 - Apple II File Type Note $50/5445, "Teach Document"
 - _Apple IIgs Toolbox Reference, Volume 3_

Teach Documents are text documents with some additional formatting commands.  The format is named
after a Macintosh program called "TeachText".

The files are essentially TextEdit control contents serialized to a file.  The data fork holds
the text, while the resource fork has two resources: an rStyleBlock (type=$8012 ID=1) with style
information that can be passed directly to TextEdit calls, and a custom resource (type=$7001 ID=1)
that holds window position information.

## File Format ##

The data fork is plain text, using one of the Macintosh characters sets, such as Mac OS Roman.

The interesting part is the rStyleBlock ($8012) resource, which uses various structures described
in _Apple IIgs Toolbox Reference, Volume 3_.  The resource contains a TextEdit `TEFormat`
structure (defined on page 49-31):
```
+$00 / 2: version - version number of structure, should be zero
+$02 / 4: rulerListLength - length, in bytes, of theRulerList
+$06 / N: theRulerList - array of TERuler structures
+$xx / 4: styleListLength - length, in bytes, of theStyleList
+$xx / N: theStyleList - array of TEStyle structures
+$xx / 4: numberOfStyles - numer of StyleItems contained in theStyles
+$xx / N: theStyles - array of StyleItems specifying which actual styles (stored in theStyleList)
          apply to which text within the TextEdit record
```
All documents appear to have a single ruler.  It's unclear how multiple rulers would be applied.

TextEdit `TERuler` structures (defined on page 49-39) are:
```
+$00 / 2: leftMargin - number of pixels to indent from left edge of text rect (exc. new para)
+$02 / 2: leftIndent - number of pixels to indent from left edge for new paragraphs
+$04 / 2: rightMargin - maximum line length, in pixels from the left edge of text rect
+$06 / 2: just - text justification (0=left, -1=right, 1=center, 2=full)
+$08 / 2: extraLS - line spacing, number of pixels to add between lines (may be negative)
+$0a / 2: flags (reserved)
+$0c / 4: userData - application-specific data
+$10 / 2: tabType - type of tab data (0=none, 1=regular intervals, 2=absolute pixel locations)
+$12 / N: theTabs - array of TabItem structures; tabTerminator field marks end of list
+$xx / 2: tabTerminator - omitted for tabType=0; pixel count for tabType=1; $ffff for tabType=2
```
These can also appear as rTERuler ($8025) resources, but not in Teach documents.  None of these
values can be set from the current Teach application.

TextEdit `TEStyle` structures (defined on page 49-41) are:
```
+$00 / 4: fontID - font manager ID
+$04 / 2: foreColor - foreground color for text
+$06 / 2: backColor - background color for text
+$08 / 4: userData - application-specific data
```
The current Teach application doesn't provide a way to set the color, but documents with colored
text have been found.

The _Apple IIgs Toolbox Reference, Volume 3_ describes `foreColor` values thusly (p.49-41):
> Foreground color for the text.  Note that all bits in TextEdit color words are significant.
> TextEdit generates QuickDraw II color patterns by replicating a color word the appropriate
> number of times for the current resolution (8 times for 640 mode, 16 times for 320 mode).
> See Chapter 16 [...] for more information on QuickDraw II patterns and dithered colors.

For example, the foreground color value 0x4444 is rendered in 640 mode as alternating
red/black pixels.

TextEdit `StyleItem` structures (defined on page 49-55) are:
```
+$00 / 4: length - total number of text characters that use this style; -1 indicates unused entry
+$04 / 4: offset - offset, in bytes, into theStyleList array to the TEStyle record
```

The `TabItem` structures (defined on page 49-59) are:
```
+$00 / 2: tabKind - must be $0000
+$02 / 2: tabData - location of absolute tab, expressed as number of pixels from left edge of view
```

Font ID records are 32-bit values, but have four distinct parts:
```
+$00 / 2: famNum - font family number
+$02 / 1: fontStyle - style of font (bit flags)
+$03 / 1: fontSize - size of font (in points, 1/72nd of an inch)
```
See chapter 8 in _Apple IIgs Toolbox Reference, Volume 1_.

### Algorithm ###

To format the text, walk through the list of StyleItem structures, applying the referenced style
to the specified number of characters.  The list of styles should exactly span the text.
