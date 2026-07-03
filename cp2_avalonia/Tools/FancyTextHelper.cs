/*
 * Copyright 2023 faddenSoft
 * Copyright 2026 Lydian Scale Software
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

using FileConv;

namespace cp2_avalonia.Tools {
    /// <summary>
    /// Converts FancyText annotations into SelectableTextBlock Inlines.
    /// </summary>
    public static class FancyTextHelper {
        /// <summary>
        /// Populates a SelectableTextBlock with FancyText content and formatting.
        /// </summary>
        private const string VIEWER_FONT_SIZE_RESOURCE = "ViewerDefaultFontSize";

        public static void Apply(SelectableTextBlock target, FancyText fancyText) {
            target.Inlines!.Clear();
            target.Text = null;
            // Reset the STB FontSize to the current configured default before deciding whether
            // this document's annotations need to override it.  Without this, FontSize=1 set by
            // a previous annotated file would persist when navigating to a no-annotation file.
            ResetFontSize(target);

            string plainText = fancyText.Text.ToString();
            var annos = new List<FancyText.Annotation>(fancyText);

            // Resolve theme-aware "inverse" brushes once. FancyText documents typically
            // express inverse text as ForeColor=White + BackColor=Black; we substitute
            // theme-appropriate values so inverse remains readable in both Light and Dark.
            IBrush? inverseFg = target.TryFindResource("ViewerInverseForegroundBrush",
                target.ActualThemeVariant, out var ifg) ? ifg as IBrush : null;
            IBrush? inverseBg = target.TryFindResource("ViewerInverseBackgroundBrush",
                target.ActualThemeVariant, out var ibg) ? ibg as IBrush : null;

            // Running state.
            bool bold = false;
            bool italic = false;
            bool underline = false;
            IBrush? foreground = null;   // null = inherit from theme (black in Light, white in Dark)
            IBrush? background = null;
            // null = let Runs inherit FontFamily from the SelectableTextBlock.
            FontFamily? fontFamily = null;

            // Determine how to handle font size for this document.
            //
            // If the document emits no FontSize annotations (e.g. AppleWorks WP), every Run
            // should inherit ViewerDefaultFontSize via the STB's
            // DynamicResource, so we leave fontSize=0 (inherit) and the STB untouched.
            //
            // If the document does emit FontSize annotations, we need Run-level sizes to be
            // the sole driver of line height. The STB's own FontSize property contributes to
            // Avalonia's line-height calculation even when all Runs have explicit sizes, so a
            // large ViewerDefaultFontSize would inflate line spacing for small-sized Runs.
            // To prevent that, we set the STB's FontSize to 1 (negligible), and initialise
            // fontSize to FancyText's documented default of 10pt so that any unannotated prefix
            // text before the first FontSize annotation is rendered at the correct base size.
            bool hasExplicitSizes = annos.Exists(a => a.Type == FancyText.AnnoType.FontSize);
            double fontSize;
            if (hasExplicitSizes) {
                target.FontSize = 1;    // prevent STB baseline from inflating small-font lines
                fontSize = 10.0;        // FancyText documented default before first annotation
            } else {
                fontSize = 0;           // inherit ViewerDefaultFontSize from the STB
            }

            int annoIdx = 0;
            int segStart = 0;

            // Emit inlines for plainText[start...end), splitting on '\n' into Run + LineBreak.
            void EmitSegment(int start, int end) {
                if (start >= end) return;
                string seg = plainText.Substring(start, end - start);
                int pos = 0;
                while (true) {
                    int nl = seg.IndexOf('\n', pos);
                    if (nl < 0) {
                        if (pos < seg.Length) {
                            AppendRun(target, seg.Substring(pos),
                                fontFamily, fontSize, bold, italic, underline,
                                foreground, background);
                        }
                        break;
                    }
                    if (nl > pos) {
                        AppendRun(target, seg.Substring(pos, nl - pos),
                            fontFamily, fontSize, bold, italic, underline,
                            foreground, background);
                    }
                    target.Inlines!.Add(new LineBreak());
                    pos = nl + 1;
                }
            }

            while (annoIdx < annos.Count) {
                FancyText.Annotation anno = annos[annoIdx];
                int annoOff = anno.Offset;

                if (annoOff > segStart) {
                    int emitEnd = annoOff <= plainText.Length ? annoOff : plainText.Length;
                    EmitSegment(segStart, emitEnd);
                    segStart = emitEnd;
                }

                ApplyAnnotationToState(anno, ref bold, ref italic, ref underline,
                    ref foreground, ref background, ref fontFamily, ref fontSize,
                    inverseFg, inverseBg);
                annoIdx++;
            }

            EmitSegment(segStart, plainText.Length);
        }

        /// <summary>
        /// Populates a SelectableTextBlock with plain text, inheriting the control's
        /// own FontFamily and FontSize so no font name is hard-coded at the call site.
        /// </summary>
        public static void ApplyPlain(SelectableTextBlock target, string text) {
            // Reset in case a previous FancyText file set FontSize=1 on this control
            // (happens when navigating between files with the prev/next arrows).
            ResetFontSize(target);
            // Pass null/0 so every Run inherits FontFamily and FontSize from the
            // SelectableTextBlock, whose DynamicResource bindings update automatically
            // when font resources are refreshed — no re-render needed for plain text.
            ApplyPlain(target, text, null, 0);
        }

        // Looks up the current ViewerDefaultFontSize resource and applies it as a literal
        // value to the STB.  This "resets" any previous local override (e.g. FontSize=1
        // set for an annotated document) without needing SetDynamicResource.
        // Because Apply/ApplyPlain are called on every navigation (and font-resource refresh),
        // event, the value will always be current.
        private static void ResetFontSize(SelectableTextBlock target) {
            if (target.TryFindResource(VIEWER_FONT_SIZE_RESOURCE,
                    target.ActualThemeVariant, out var szRes) && szRes is double defaultSize) {
                target.FontSize = defaultSize;
            }
        }

        /// <summary>
        /// Populates a SelectableTextBlock with plain text using an explicit font override.
        /// Pass <c>null</c> / <c>0</c> for font / fontSize to inherit from the control.
        /// </summary>
        private static void ApplyPlain(SelectableTextBlock target, string text,
                FontFamily? font, double fontSize) {
            target.Inlines!.Clear();
            target.Text = null;

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++) {
                if (lines[i].Length > 0) {
                    AppendRun(target, lines[i], font, fontSize,
                        false, false, false, null, null);
                }
                if (i < lines.Length - 1) {
                    target.Inlines!.Add(new LineBreak());
                }
            }
        }

        private static void AppendRun(SelectableTextBlock target, string text,
                FontFamily? fontFamily, double fontSize,
                bool bold, bool italic, bool underline,
                IBrush? foreground, IBrush? background) {
            var run = new Run(text) {
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
            };
            // Only set FontFamily/FontSize when explicitly overridden by an annotation.
            // Leaving them unset lets the Run inherit from the parent SelectableTextBlock,
            // which has DynamicResource bindings that update when font settings change.
            if (fontFamily != null) {
                run.FontFamily = fontFamily;
            }
            if (fontSize > 0) {
                run.FontSize = fontSize;
            }
            if (foreground != null) {
                run.Foreground = foreground;
            }
            if (background != null) {
                run.Background = background;
            }
            if (underline) {
                run.TextDecorations = TextDecorations.Underline;
            }
            target.Inlines!.Add(run);
        }

        private static void ApplyAnnotationToState(FancyText.Annotation anno,
                ref bool bold, ref bool italic, ref bool underline,
                ref IBrush? foreground, ref IBrush? background,
                ref FontFamily? fontFamily, ref double fontSize,
                IBrush? inverseFg, IBrush? inverseBg) {
            switch (anno.Type) {
                case FancyText.AnnoType.Bold:
                    bold = (bool)anno.Data!;
                    break;
                case FancyText.AnnoType.Italic:
                    italic = (bool)anno.Data!;
                    break;
                case FancyText.AnnoType.Underline:
                    underline = (bool)anno.Data!;
                    break;
                case FancyText.AnnoType.ForeColor: {
                    int argb = (int)anno.Data!;
                    byte a = (byte)(argb >> 24);
                    byte r = (byte)(argb >> 16);
                    byte g = (byte)(argb >> 8);
                    byte b = (byte)argb;
                    if (a == 0) a = 255;
                    // Treat pure black as "default foreground" so the theme's text color
                    // shows through (black in Light, white in Dark).
                    if (r == 0 && g == 0 && b == 0) {
                        foreground = null;
                    } else if (r == 255 && g == 255 && b == 255 && inverseFg != null) {
                        // Pure white is typically the inverse-text foreground; map to a
                        // theme-aware brush so it stays readable on the inverse background.
                        foreground = inverseFg;
                    } else {
                        foreground = new SolidColorBrush(new Color(a, r, g, b));
                    }
                    break;
                }
                case FancyText.AnnoType.BackColor: {
                    int argb = (int)anno.Data!;
                    byte a = (byte)(argb >> 24);
                    byte r = (byte)(argb >> 16);
                    byte g = (byte)(argb >> 8);
                    byte b = (byte)argb;
                    if (a == 0) a = 255;
                    // Pure white means "no background" — inherit the viewer's theme bg.
                    if (r == 255 && g == 255 && b == 255) {
                        background = null;
                    } else if (r == 0 && g == 0 && b == 0 && inverseBg != null) {
                        // Pure black is the inverse-text background; map to a theme-aware
                        // brush (black in Light, white in Dark).
                        background = inverseBg;
                    } else {
                        background = new SolidColorBrush(new Color(a, r, g, b));
                    }
                    break;
                }
                case FancyText.AnnoType.FontFamily: {
                    FancyText.FontFamily family = (FancyText.FontFamily)anno.Data!;
                    if (family.Name.Equals("Symbol", System.StringComparison.OrdinalIgnoreCase)) {
                        // Symbol is a specific named font with no meaningful cross-platform
                        // substitute; keep the name literal so glyph-mapped documents render.
                        fontFamily = new FontFamily("Symbol");
                    } else if (family.IsMono) {
                        fontFamily = family.IsSerif
                            ? new FontFamily("Courier New, Courier, Liberation Mono, DejaVu Sans Mono, monospace")
                            : new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, Menlo, monospace");
                    } else {
                        fontFamily = family.IsSerif
                            ? new FontFamily("Georgia, Times New Roman, Liberation Serif, Times, serif")
                            : new FontFamily("Arial, Helvetica, Liberation Sans, sans-serif");
                    }
                    break;
                }
                case FancyText.AnnoType.FontSize: {
                    int pts = (int)anno.Data!;
                    if (pts > 0) {
                        fontSize = pts;
                    }
                    break;
                }

                // NewParagraph / NewPage / Tab / Outline / Shadow / Superscript / Subscript /
                // Justification / LeftMargin / RightMargin: silently skip.
                default:
                    break;
            }
        }
    }
}
