/*
 * Copyright 2022 faddenSoft
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
using System;
using System.Diagnostics;
using System.Text;

namespace CommonUtil {
    /// <summary>
    /// Pathname utility functions.
    /// </summary>
    public static class PathName {
        /// <summary>
        /// Default replacement character.
        /// </summary>
        /// <remarks>
        /// <para>Question mark ('?') feels most natural, but can't be used on Windows.
        /// The Win32 port of "unzip" also uses underscore.</para>
        /// </remarks>
        public const char DEFAULT_REPL_CHAR = '_';

        /// <summary>
        /// If you cannot afford a filename, one will be provided for you.
        /// </summary>
        public const string MIRANDA_FILENAME = "A";

        /// <summary>
        /// Character to use when no directory separator is defined.
        /// </summary>
        /// <remarks>
        /// This is Unicode noncharacter-FFFE, which is guaranteed not to be a valid character.
        /// </remarks>
        public const char NO_DIR_SEP = '\ufffe';

        /// <summary>
        /// Invalid filename characters.  System-specific.
        /// </summary>
        /// <remarks>
        /// <para>Windows: the control characters (0x00-0x1f) and |/\?*:&lt;&gt;&quot; .</para>
        /// <para>UNIX: '\0' and '/'.</para>
        /// </remarks>
        private static readonly char[] INVALID_CHARS = Path.GetInvalidFileNameChars();

        /// <summary>
        /// Character to use for forming NAPS escape codes.
        /// </summary>
        private const char NAPS_ESC_CHAR = '%';

        /// <summary>
        /// Comparison algorithm to use for filenames.
        /// </summary>
        /// <remarks>
        /// Brief explanation of OrdinalIgnoreCase vs. InvariantCultureIgnoreCase here:
        /// <see href="https://stackoverflow.com/a/2749678/294248"/>.
        /// </remarks>
        public enum CompareAlgorithm {
            Unknown = 0,

            // Sort based on each character's numeric value.
            Ordinal,

            // Sort based on character value, ignoring case.
            OrdinalIgnoreCase,

            // Sort based on character value, ignoring case, but applying some additional
            // rules for strings with characters like 'é' or 'ö'.
            InvariantCultureIgnoreCase,

            // Sort based on HFS filename rules.
            HFSFileName,
        }

        /// <summary>
        /// Compares two pathnames, using the specified comparison rules.
        /// </summary>
        /// <remarks>
        /// <para>Use this when comparing pathnames that may use different filename separator
        /// characters, e.g. comparing "foo:bar" to "foo/bar".</para>
        /// </remarks>
        /// <param name="path1">First pathname.</param>
        /// <param name="fssep1">Filename separator character for first pathname.</param>
        /// <param name="path2">Second pathname.</param>
        /// <param name="fssep2">Filename separator character for second pathname.</param>
        /// <param name="alg">Algorithm to use when comparing filename components.</param>
        /// <returns>Less than zero if path1 precedes path2 in lexical sort order, zero if it's
        ///   in the same position, or greater than zero if path1 follows path2.</returns>
        public static int ComparePathNames(string path1, char fssep1, string path2, char fssep2,
                CompareAlgorithm alg) {
            string[] names1 = path1.Split(fssep1);
            string[] names2 = path2.Split(fssep2);

            for (int i = 0; i < Math.Max(names1.Length, names2.Length); i++) {
                if (i >= names1.Length) {
                    return -1;      // ran out of names1, path1 comes first
                }
                if (i >= names2.Length) {
                    return 1;       // ran out of names2, path2 comes first
                }

                int cmp;
                switch (alg) {
                    case CompareAlgorithm.Ordinal:
                        cmp = string.Compare(names1[i], names2[i],
                            StringComparison.Ordinal);
                        break;
                    case CompareAlgorithm.OrdinalIgnoreCase:
                        cmp = string.Compare(names1[i], names2[i],
                            StringComparison.OrdinalIgnoreCase);
                        break;
                    case CompareAlgorithm.InvariantCultureIgnoreCase:
                        cmp = string.Compare(names1[i], names2[i],
                            StringComparison.InvariantCultureIgnoreCase);
                        break;
                    case CompareAlgorithm.HFSFileName:
                        cmp = MacChar.CompareHFSFileNames(names1[i], names2[i]);
                        break;
                    default:
                        throw new ArgumentException("Invalid compare algorithm: " + alg);
                }
                if (cmp != 0) {
                    return cmp;
                }
            }

            return 0;
        }

        /// <summary>
        /// Strips the directory names out of a pathname, leaving only the filename.  If the
        /// pathname ends with a separator character (e.g. "/usr/local/"), an empty string will
        /// be returned.
        /// </summary>
        /// <param name="pathName">Full or partial pathname.</param>
        /// <param name="dirSep">Directory separator character used for this pathname.</param>
        /// <returns></returns>
        public static string GetFileName(string pathName, char dirSep) {
            int lastIndex = pathName.LastIndexOf(dirSep);
            if (lastIndex < 0 || lastIndex == pathName.Length - 1) {
                return string.Empty;
            } else {
                return pathName.Substring(lastIndex + 1);
            }
        }

        /// <summary>
        /// Splits a partial path into components.  Directories are separated by any of the
        /// characters in <paramref name="dirSepChars"/>.  Separator characters may be escaped
        /// with a backslash.
        /// </summary>
        /// <param name="pathName">Pathname to split.</param>
        /// <param name="dirSepChars">Directory separator characters.</param>
        /// <returns>List of components.</returns>
        /// <exception cref="ArgumentException">An empty path component was found.</exception>
        public static List<string> SplitPartialPath(string pathName, char[] dirSepChars) {
            // Split pathname into a list of path components, separating strings when we encounter
            // one of the path separator chars.  Don't split if the separator is quoted with a
            // backslash.
            List<string> components = new List<string>();
            bool esc = false;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < pathName.Length; i++) {
                char ch = pathName[i];
                if (esc) {
                    // Previous char was '\'.  If this is a dir sep char, output this character
                    // and discard the backslash.  Otherwise, output both the backslash and the
                    // character, so we can escape wildcards.
                    if (!dirSepChars.Contains(ch)) {
                        sb.Append('\\');
                    }
                    sb.Append(ch);
                    esc = false;
                } else if (ch == '\\') {
                    // Found a backslash.  Don't output it, but escape the next thing.  If it's
                    // at the end of the string, we just ignore it.
                    esc = true;
                } else {
                    // Check to see if it's one of the directory separator characters.
                    if (dirSepChars.Contains(ch)) {
                        if (sb.Length == 0 && components.Count != 0) {
                            // Empty path component, not at start.
                            throw new ArgumentException("invalid pathname: '" +
                                pathName + "'");
                        }
                        components.Add(sb.ToString());
                        sb.Clear();
                    } else {
                        sb.Append(ch);
                    }
                }
            }
            if (sb.Length == 0) {
                // Ended with a dir separator.  ignore it.
                Debug.WriteLine("Found trailing path separator in '" + pathName + "'");
            } else {
                components.Add(sb.ToString());
            }

            return components;
        }

        /// <summary>
        /// Converts any ASCII control characters (0x00-0x1f, 0x7f) to the Unicode "Control
        /// Pictures" range (U+2400 - U+243F).
        /// </summary>
        /// <param name="str">String to convert.</param>
        /// <returns>Converted string.</returns>
        public static string PrintifyControlChars(string str) {
            StringBuilder sb = new StringBuilder(str.Length);
            foreach (char ch in str) {
                sb.Append(ASCIIUtil.MakePrintable(ch));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Adjusts a pathname to be valid on the host system.  The pathname will use the
        /// host's preferred directory component separator character.
        /// </summary>
        /// <param name="pathName">Pathname to adjust.</param>
        /// <param name="dirSep">Directory separator character used in pathname.</param>
        /// <param name="repl">Replacement character to use.</param>
        /// <returns>Adjusted pathname.</returns>
        public static string AdjustPathName(string pathName, char dirSep, char repl) {
            StringBuilder sb = new StringBuilder(pathName.Length);
            string[] names = pathName.Split(dirSep);
            for (int i = 0; i < names.Length; i++) {
                sb.Append(AdjustFileName(names[i], repl));
                if (i != names.Length - 1) {
                    sb.Append(Path.DirectorySeparatorChar);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Adjusts a filename (not a pathname) to be valid on the host system.
        /// </summary>
        /// <remarks>
        /// <para>.NET provides <see cref="Path.GetInvalidPathChars"/> and
        /// <see cref="Path.GetInvalidFileNameChars"/>.  On Windows, they both list 0x00-0x1f
        /// and '|', and the filename version also lists the path separators ('/', '\'), shell
        /// wildcards ('?', '*'), and some other stuff (':', '&lt;', '&gt;', '"').  Most of the
        /// characters other than the path separators are actually valid on NTFS, but Win32
        /// reserves them.</para>
        /// <para>We want to replace anything that simply won't work with another value, and
        /// keep all the rest.</para>
        /// <para>If the filename is an empty string, provide something.</para>
        /// </remarks>
        /// <param name="fileName">Filename to adjust.</param>
        /// <param name="repl">Replacement character to use.</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName, char repl) {
            if (fileName.Length == 0) {
                return MIRANDA_FILENAME;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < fileName.Length; i++) {
                char ch = fileName[i];
                for (int ici = 0; ici < INVALID_CHARS.Length; ici++) {
                    if (ch == INVALID_CHARS[ici]) {
                        ch = repl;
                        break;
                    }
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Escapes a pathname to make it valid on the host system, using NAPS-style '%xx'.
        /// The pathname will use the host's preferred directory component separator character.
        /// This is deprecated -- we only escape the filename now.
        /// </summary>
        /// <param name="pathName">Pathname to escape.</param>
        /// <param name="dirSep">Directory separator character used in pathname.</param>
        /// <returns>Adjusted pathname.</returns>
        public static string EscapePathName(string pathName, char dirSep) {
            StringBuilder sb = new StringBuilder(pathName.Length);
            string[] names = pathName.Split(dirSep);
            foreach (string name in names) {
                if (sb.Length != 0) {
                    sb.Append(Path.DirectorySeparatorChar);
                }
                sb.Append(EscapeFileName(name));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Adjusts a pathname to make it valid on the host system, performing NAPS-style "%xx"
        /// escaping on the filename component.
        /// The pathname will use the host's preferred directory component separator character.
        /// </summary>
        /// <param name="pathName">Pathname to adjust.</param>
        /// <param name="dirSep">Directory separator character used in pathname.</param>
        /// <param name="repl">Replacement character to use.</param>
        /// <returns>Adjusted pathname.</returns>
        public static string AdjustEscapePathName(string pathName, char dirSep, char repl) {
            StringBuilder sb = new StringBuilder(pathName.Length);
            string[] names = pathName.Split(dirSep);
            for (int i = 0; i < names.Length; i++) {
                string name = names[i];
                if (i == names.Length - 1) {
                    sb.Append(EscapeFileName(name));
                } else {
                    sb.Append(AdjustFileName(name, repl));
                    sb.Append(Path.DirectorySeparatorChar);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Escapes a filename (not a pathname) to be valid on the host system.
        /// </summary>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename.</returns>
        public static string EscapeFileName(string fileName) {
            if (fileName.Length == 0) {
                return MIRANDA_FILENAME;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < fileName.Length; i++) {
                char ch = fileName[i];
                bool doEscape = false;
                for (int ici = 0; ici < INVALID_CHARS.Length; ici++) {
                    if (ch == INVALID_CHARS[ici]) {
                        doEscape = true;
                        break;
                    }
                }
                ch = ASCIIUtil.MakeUnprintable(ch);         // undo printable conversion
                if (ch < 0x1f || ch == 0x7f) {              // always escape C0 ctrl chars and DEL
                    doEscape = true;
                }
                if (doEscape) {
                    Debug.Assert(ch >= 0 && ch <= 127);     // invalids are expected to be ASCII
                    sb.Append(NAPS_ESC_CHAR);
                    sb.Append(((int)ch).ToString("x2"));
                } else {
                    sb.Append(ch);
                }
                if (ch == NAPS_ESC_CHAR) {
                    // Convert '%' to "%%".
                    Debug.Assert(!doEscape);
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Un-escapes a NAPS-escaped partial pathname.  This is coming from the host system,
        /// destined for an archive.
        /// This is deprecated -- we only escape the filename now.
        /// </summary>
        /// <remarks>
        /// <para>We want to pull the path apart, un-escape each individual filename, then put
        /// it back together with the directory separator preferred by the target file archive
        /// or filesystem.  Using the specific target separator allows us to include the
        /// full range of characters, e.g. '/' in HFS filenames.</para>
        /// </remarks>
        /// <param name="pathName">Pathname to convert.</param>
        /// <param name="pathDirSep">Host path directory separator.</param>
        /// <returns>Un-escaped string.</returns>
        public static string UnescapePathName(string pathName, char pathDirSep, char newDirSep) {
            StringBuilder sb = new StringBuilder(pathName.Length);
            string[] names = pathName.Split(pathDirSep);
            for (int i = 0; i < names.Length; i++) {
                sb.Append(UnescapeFileName(names[i], newDirSep));
                if (i != names.Length - 1) {
                    sb.Append(newDirSep);
                }
            }
            return sb.ToString();
        }

        private static int GetHexDigit(char val) {
            if (val >= '0' && val <= '9') {
                return val - '0';
            } else if (val >= 'A' && val <= 'F') {
                return 10 + val - 'A';
            } else if (val >= 'a' && val <= 'f') {
                return 10 + val - 'a';
            } else {
                return -1;
            }
        }

        /// <summary>
        /// Un-escapes a NAPS-encoded filename.
        /// </summary>
        /// <remarks>
        /// <para>A value for <paramref name="newDirSep"/> should be provided when this is called
        /// as part of unescaping a full pathname, because we don't want individual directory
        /// components to have separator characters in them.  If we are unescaping an isolated
        /// filename, which will be corrected by a filesystem-specific "adjust" function, there's
        /// no need to elide the character.  Pass in NO_DIR_SEP instead.</para>
        /// </remarks>
        /// <param name="fileName">Filename (not pathname) to un-escape.</param>
        /// <param name="newDirSep">Directory separator character; treat as invalid.</param>
        /// <returns>Converted string.</returns>
        public static string UnescapeFileName(string fileName, char newDirSep) {
            // INVALID_CHARS is not useful here because we're creating a path for an archive,
            // not the host system.
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < fileName.Length; i++) {
                bool didSub = false;
                if (fileName[i] == NAPS_ESC_CHAR) {
                    if (i < fileName.Length - 1 && fileName[i + 1] == '%') {
                        // Convert "%%" to '%'.
                        didSub = true;
                        sb.Append(NAPS_ESC_CHAR);
                        i += 1;
                    } else if (i < fileName.Length - 2) {
                        int hi = GetHexDigit(fileName[i + 1]);
                        int lo = GetHexDigit(fileName[i + 2]);
                        if (hi >= 0 && lo >= 0) {
                            didSub = true;
                            char unch = (char)(hi << 4 | lo);
                            if (unch == 0) {
                                // "%00", omit from output
                            } else if (newDirSep != NO_DIR_SEP && unch == newDirSep) {
                                // Replace directory separator with single '%'.
                                sb.Append(NAPS_ESC_CHAR);
                            } else {
                                sb.Append(unch);
                            }
                            i += 2;
                        }
                    }
                }
                if (!didSub) {
                    sb.Append(fileName[i]);
                }
            }
            return sb.ToString();
        }

        #region Unit tests

        /// <summary>
        /// Unit test.  Asserts on failure.
        /// </summary>
        public static bool DebugTest() {
            int result;

            result = ComparePathNames("abcd", '/', "abcd", '/',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result == 0);
            result = ComparePathNames("abcd", '/', "ABCD", '/',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result > 0);
            result = ComparePathNames("abcd", '/', "ABCD", '/',
                CompareAlgorithm.OrdinalIgnoreCase);
            Debug.Assert(result == 0);

            result = ComparePathNames("foo/bar", '/', "foo:bar", ':',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result == 0);
            result = ComparePathNames("foo/BAR", '/', "foo:bar", ':',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result < 0);
            result = ComparePathNames("foo/bar", '/', "foo:BAR", ':',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result > 0);
            result = ComparePathNames("foo/BAR", '/', "foo:bar", ':',
                CompareAlgorithm.OrdinalIgnoreCase);
            Debug.Assert(result == 0);

            result = ComparePathNames("foo/bar", '/', "foo:bar", '/',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result < 0);
            result = ComparePathNames("foo/bar/ack", '/', "foo:bar", ':',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result > 0);
            result = ComparePathNames("foo/bar", '/', "foo:bar:ack", ':',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result < 0);

            result = ComparePathNames("äà/ÅÃ", '/', "ÄÀ:åã", ':',
                CompareAlgorithm.HFSFileName);
            Debug.Assert(result == 0);
            result = ComparePathNames("Ä", '/', "b", '/',
                CompareAlgorithm.Ordinal);
            Debug.Assert(result > 0);       // 0x80 > 'b'
            result = ComparePathNames("Ä", '/', "b", '/',
                CompareAlgorithm.OrdinalIgnoreCase);
            Debug.Assert(result > 0);       // 0x80 > 'B'
            result = ComparePathNames("Ä", '/', "B", '/',
                CompareAlgorithm.InvariantCultureIgnoreCase);
            Debug.Assert(result < 0);       // 'A' variants are < 'B'
            result = ComparePathNames("Ä", '/', "B", '/',
                CompareAlgorithm.HFSFileName);
            Debug.Assert(result < 0);       // 'A' variants are < 'B'


            string fancyName = "ABCabcäà_ÅÃ";
            Debug.Assert(AdjustFileName(fancyName, '_') == fancyName);
            Debug.Assert(AdjustFileName("foo/bar", '_') == "foo_bar");
            Debug.Assert(AdjustFileName("foo:bar", '_') == "foo_bar");

            Debug.Assert(EscapeFileName(fancyName) == fancyName);
            Debug.Assert(EscapeFileName("a/b") == "a%2fb");
            Debug.Assert(EscapeFileName("a%b") == "a%%b");
            Debug.Assert(EscapeFileName(@"face/off:dir\name") == "face%2foff%3adir%5cname");
            Debug.Assert(EscapeFileName("x\r\ny\u007fz") == "x%0d%0ay%7fz");
            Debug.Assert(EscapeFileName("x\u240d\u240ay\u2421z") == "x%0d%0ay%7fz");

            Debug.Assert(UnescapeFileName(fancyName, '/') == fancyName);
            Debug.Assert(UnescapeFileName("hi%3athere", ':') == "hi%there");
            Debug.Assert(UnescapeFileName("%3F", '/') == "?");
            Debug.Assert(UnescapeFileName("face%2foff%3adir%5cname", '/') == @"face%off:dir\name");
            Debug.Assert(UnescapeFileName("non%00exist", '/') == "nonexist");
            Debug.Assert(UnescapeFileName("a%%b", '/') == "a%b");

            char dirSep = Path.DirectorySeparatorChar;
            Debug.Assert(EscapePathName("a:b/c:d", '/') == "a%3ab" + dirSep + "c%3ad");
            Debug.Assert(AdjustEscapePathName("a:b/c:d", '/', '_') == "a_b" + dirSep + "c%3ad");
            Debug.Assert(AdjustEscapePathName("%%xyz:foo/bar", ':', '_') ==
                "%%xyz" + dirSep + "foo%2fbar");

            Debug.Assert(UnescapePathName(@"C:\foo\bar\ack", '\\', '/') == @"C:/foo/bar/ack");
            Debug.Assert(UnescapePathName(@"/foo/bar/ack", '/', '\\') == @"\foo\bar\ack");
            Debug.Assert(UnescapePathName("a%3ab/c%3ad", '/', '/') == "a:b/c:d");
            string troubledName = @"face/off:dir\name?";
            string escapeTrouble = EscapePathName(troubledName, '=');
            Debug.Assert(UnescapePathName(escapeTrouble, '=', '=') == @"face/off:dir\name?");

            Debug.Assert(PrintifyControlChars("x\r\ny\u007fz") == "x\u240d\u240ay\u2421z");
            Debug.Assert(PrintifyControlChars("x\u240d\u240ay\u2421z") == "x\u240d\u240ay\u2421z");

            Debug.WriteLine("PathName check OK");
            return true;
        }

        #endregion Unit tests
    }
}
