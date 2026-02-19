/*
 * Copyright 2023 faddenSoft
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
using System.Text.RegularExpressions;

namespace CommonUtil {
    /// <summary>
    /// Support for filename "glob" pattern matching.  '*' and '?' can be used the same way
    /// they would from a command-line shell.
    /// </summary>
    /// <remarks>
    /// <para>Various systems use '/', ':', or '\' to separate directory path components,
    /// often allowing the other characters to be valid in filenames, so we need to take that
    /// into account when matching.  We can't simply operate on whole strings, because "*"
    /// should only match one path component.</para>
    /// <para>Does not currently handle set specifiers like "*.[ch]".  Does not currently
    /// handle multi-directory "**".</para>
    /// </remarks>
    public class Glob {
        /// <summary>
        /// Standard directory separator characters used in file archives and disk images.
        /// These are what we're expecting the user to type on the command line.
        /// </summary>
        public static readonly char[] STD_DIR_SEP_CHARS = new char[] { '/', ':' };

        /// <summary>
        /// Character to use when no directory separator is defined.
        /// </summary>
        internal const char NO_DIR_SEP = PathName.NO_DIR_SEP;

        /// <summary>
        /// Original pattern string.
        /// </summary>
        public string Pattern { get; private set; }

        /// <summary>
        /// True if <see cref="IsMatch(string, char, bool)"/> has ever returned true.
        /// </summary>
        public bool HasMatched { get; private set; }

        /// <summary>
        /// List of regular expressions, one per pathname component.
        /// </summary>
        private List<Regex> mRegexes;


        /// <summary>
        /// Constructs a new glob matcher, using the specified pattern.
        /// </summary>
        /// <param name="pattern">Pattern string, e.g. "foo/.??*/*.txt".</param>
        /// <param name="dirSepChars">Characters used to separate directory names.  May be
        ///   empty.</param>
        /// <param name="ignoreCase">If set, the match patterns will ignore case.</param>
        /// <exception cref="ArgumentException">An empty path component was found.</exception>
        public Glob(string pattern, char[] dirSepChars, bool ignoreCase) {
            Pattern = pattern;
            mRegexes = new List<Regex>();

            // Special case an empty string.  AppleSingle can have empty filenames.
            if (string.IsNullOrEmpty(pattern)) {
                mRegexes.Add(new Regex("^$"));
                return;
            }

            List<string> components = PathName.SplitPartialPath(pattern, dirSepChars, true);

            // Create regular expressions for each component.
            foreach (string component in components) {
                mRegexes.Add(GenerateGlobMatcher(component,
                    RegexOptions.Singleline | (ignoreCase ? RegexOptions.IgnoreCase : 0)));
            }
            Debug.Assert(mRegexes.Count != 0);
        }


        /// <summary>
        /// Determines whether the pathname matches the glob pattern.
        /// </summary>
        /// <remarks>
        /// <para>We don't try to look for escaped directory separators because these names
        /// should be coming out of a disk image or file archive, which won't have escaped
        /// pathnames.</para>
        /// <para>Allowing prefix matches means we can take "foo" as a pattern and match
        /// against "foo/bar", "foo/bar/ack", and so on.</para>
        /// </remarks>
        /// <param name="pathName">Pathname to test.</param>
        /// <param name="dirSep">Directory separator character used in pathname.  Use
        ///   <see cref="NO_DIR_SEP"/> if the pathname is a single filename.</param>
        /// <param name="prefixOkay">Set to true if matching a prefix is enough, false if
        ///   an exact match is required.</param>
        /// <returns>True if it's a match.</returns>
        public bool IsMatch(string pathName, char dirSep, bool prefixOkay = false) {
            string[] components;
            if (dirSep == NO_DIR_SEP) {
                components = new string[] { pathName };
            } else {
                if (pathName.Length > 0 && pathName[pathName.Length - 1] == dirSep) {
                    // Trim trailing '/', found in ZIP directory names.
                    pathName = pathName.Substring(0, pathName.Length - 1);
                }
                components = pathName.Split(dirSep);
            }
            if (components.Length < mRegexes.Count ||
                    (!prefixOkay && components.Length != mRegexes.Count)) {
                return false;
            }
            for (int i = 0; i < mRegexes.Count; i++) {
                if (!mRegexes[i].IsMatch(components[i])) {
                    //Debug.WriteLine("Did not match: '" + components[i] + "' " + mRegexes[i]);
                    return false;
                }
            }
            HasMatched = true;
            return true;
        }

        /// <summary>
        /// Generates a regular expression string for matching the glob pattern.
        /// </summary>
        /// <remarks>
        /// <para>The pattern string may be empty, e.g. for the first component of an absolute
        /// pathname ("/foo").</para>
        /// <para>A simple approach like https://stackoverflow.com/a/4146349/294248 won't work
        /// for us, because we need to handle escaped wildcards.  Regex.Escape() converts "*" to
        /// "\*", and "\*" to "\\\*" so we need to count backslashes in the escaped string.</para>
        /// </remarks>
        /// <param name="pattern">Glob pattern string.</param>
        /// <returns>Regular expression string.</returns>
        public static string GenerateGlobExpression(string pattern) {
            if (!string.IsNullOrEmpty(pattern)) {
                pattern = Regex.Escape(pattern);            // escape regex meta-chars
                //pattern = pattern.Replace(@"\*", ".*");     // replace escaped '*' with ".*"
                //pattern = pattern.Replace(@"\?", ".");      // replaced escaped '?' with "."

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < pattern.Length; i++) {
                    bool didReplace = false;
                    int remaining = pattern.Length - i - 1;
                    if (remaining > 0 && pattern[i] == '\\') {
                        // Next character is escaped.  Is it double-escaped?
                        if (remaining > 2 && pattern[i + 1] == '\\' && pattern[i + 2] == '\\') {
                            // Next character is double-escaped.  Is it a wildcard?
                            if (pattern[i + 3] == '*' || pattern[i + 3] == '?') {
                                // The user escaped a wildcard.  Replace "\\\x" with "\x".
                                sb.Append('\\');
                                sb.Append(pattern[i + 3]);
                                i += 3;
                                didReplace = true;
                            }
                        } else if (pattern[i + 1] == '*') {
                            // Replace "\*" with ".*".
                            sb.Append(".*");
                            i++;
                            didReplace = true;
                        } else if (pattern[i + 1] == '?') {
                            // Replace "\?" with ".".
                            sb.Append(".");
                            i++;
                            didReplace = true;
                        }
                    }

                    if (!didReplace) {
                        sb.Append(pattern[i]);
                    }
                }
                pattern = sb.ToString();
            }
            return "^" + pattern + "$";                     // match full string only
        }

        /// <summary>
        /// Generates a regular expression for matching the glob pattern.
        /// </summary>
        /// <param name="pattern">Glob pattern string.</param>
        /// <param name="options">Regular expression options.</param>
        /// <returns>Regular expression.</returns>
        public static Regex GenerateGlobMatcher(string pattern, RegexOptions options) {
            return new Regex(GenerateGlobExpression(pattern), options);
        }

        /// <summary>
        /// Generates a list of regular expressions that match the provided pathname patterns.
        /// </summary>
        /// <param name="patterns">Array of pathname pattern strings.  May be empty.</param>
        /// <param name="pathSepChars">Pathname directory separator characters used in the
        ///   pattern string.  May be empty.</param>
        /// <param name="ignoreCase">If set, the match patterns will ignore case.</param>
        /// <returns>List of regular expressions.</returns>
        public static List<Glob> GenerateGlobSet(string[] patterns, char[] pathSepChars,
                bool ignoreCase) {
            List<Glob> result = new List<Glob>();
            foreach (string pattern in patterns) {
                Glob gl = new Glob(pattern, pathSepChars, ignoreCase);
                result.Add(gl);
            }
            return result;
        }

        public override string ToString() {
            StringBuilder sb = new StringBuilder();
            foreach (Regex re in mRegexes) {
                if (sb.Length != 0) {
                    sb.Append(" | ");
                }
                sb.Append(re.ToString());
            }
            return sb.ToString();
        }

        #region Unit tests

        private static readonly char[] EMPTY_CHAR_ARRAY = new char[0];
        private static readonly char[] STD_SEP_CHARS = new char[] { ':', '/' };

        /// <summary>
        /// Unit test.  Asserts on failure.
        /// </summary>
        public static bool DebugTest() {
            Debug.Assert(GenerateGlobExpression("*") == "^.*$");
            Debug.Assert(GenerateGlobExpression("?") == "^.$");
            Debug.Assert(GenerateGlobExpression(@"\") == @"^\\$");
            Debug.Assert(GenerateGlobExpression(@"a\*b\?c") == @"^a\*b\?c$");
            Debug.Assert(GenerateGlobExpression("f?o") == "^f.o$");

            {
                Regex match = GenerateGlobMatcher("foo", RegexOptions.None);
                Debug.Assert(match.IsMatch("foo"));
                Debug.Assert(!match.IsMatch("bar"));
                Debug.Assert(!match.IsMatch("fooo"));
                Debug.Assert(!match.IsMatch("afoo"));
                Debug.Assert(!match.IsMatch("Foo"));
            }
            {
                Regex match = GenerateGlobMatcher("f?o", RegexOptions.None);
                Debug.Assert(match.IsMatch("fOo"));
                Debug.Assert(match.IsMatch("f/o"));
                Debug.Assert(!match.IsMatch("fo?"));
                Debug.Assert(!match.IsMatch("FOO"));
            }
            {
                Glob match = new Glob("single/value:yay", EMPTY_CHAR_ARRAY, true);
                Debug.Assert(match.IsMatch("single/value:yay", NO_DIR_SEP));
                Debug.Assert(match.IsMatch("Single/value:yay", NO_DIR_SEP));
                Debug.Assert(!match.IsMatch("single/value/yay", NO_DIR_SEP));
            }
            {
                Glob match = new Glob("foo/bar", STD_SEP_CHARS, false);
                Debug.Assert(match.IsMatch("foo/bar", '/'));
                Debug.Assert(match.IsMatch("foo:bar", ':'));
                Debug.Assert(!match.IsMatch("foo|bar", '/'));
                Debug.Assert(!match.IsMatch("foo/bar", ':'));
            }
            {
                Glob match = new Glob("f*/b?r", STD_SEP_CHARS, false);
                Debug.Assert(match.IsMatch("foo/bar", '/'));
                Debug.Assert(match.IsMatch("fudge:bor", ':'));
                Debug.Assert(!match.IsMatch("foo|bar", '/'));
            }
            {
                Glob match = new Glob("*a:*b:*c", STD_SEP_CHARS, false);
                Debug.Assert(match.IsMatch("a/b/c", '/'));
                Debug.Assert(match.IsMatch("cba:cab:abc", ':'));
                Debug.Assert(!match.IsMatch("A/B/C", '/'));
                Debug.Assert(!match.IsMatch("abc", ':'));
            }
            {
                Glob match = new Glob(".??*/*.txt", STD_SEP_CHARS, true);
                Debug.Assert(match.IsMatch(".DIR/FOO.TXT", '/'));
                Debug.Assert(!match.IsMatch("../foo.Txt", '/'));
                Debug.Assert(!match.IsMatch(".a/foo.txt", '/'));
            }
            {
                Glob match = new Glob("*", STD_SEP_CHARS, true);
                Debug.Assert(match.IsMatch("Blah", '/'));
                Debug.Assert(match.IsMatch("", '/'));
                Debug.Assert(match.IsMatch("/", '/'));                  // trailing slash ignored
                Debug.Assert(!match.IsMatch("//", '/'));
                Debug.Assert(!match.IsMatch("Blah/Foo", '/'));
            }
            {
                Glob match = new Glob("a/b/*/", STD_SEP_CHARS, true);   // trailing slash ignored
                Debug.Assert(match.IsMatch("a:b:c", ':'));
                Debug.Assert(!match.IsMatch("a:b", ':'));
                Debug.Assert(!match.IsMatch("a:b:c:d", ':'));
            }
            {
                Glob match = new Glob("/a/b/*", STD_SEP_CHARS, true);
                Debug.Assert(match.IsMatch(":a:b:c", ':'));
                Debug.Assert(!match.IsMatch("a:b:c", ':'));
            }
            {
                Glob match = new Glob(@"a\/b/c", STD_SEP_CHARS, true);  // escaped dir separator
                Debug.Assert(match.IsMatch("A/b:c", ':'));
                Debug.Assert(!match.IsMatch("a/b/c", '/'));
            }
            {
                Glob match = new Glob("a/b", STD_SEP_CHARS, true);
                Debug.Assert(match.IsMatch("a:b", ':', true));
                Debug.Assert(match.IsMatch("a:b:c", ':', true));
                Debug.Assert(match.IsMatch("a:b:c:d", ':', true));
                Debug.Assert(!match.IsMatch("a", ':', true));
                Debug.Assert(!match.IsMatch("a:b:c", ':', false));
            }
            {
                Glob match = new Glob("a", STD_SEP_CHARS, true);
                Debug.Assert(match.IsMatch("a", ':', true));
                Debug.Assert(match.IsMatch("a:b", ':', true));
                Debug.Assert(match.IsMatch("a:b:c", ':', true));
            }
            {
                Glob match = new Glob(@"Foo\*bar\?", STD_SEP_CHARS, true);  // escaped wildcards
                Debug.Assert(match.IsMatch("Foo*bar?", '/', true));
                Debug.Assert(!match.IsMatch("Foo!bar?", '/', true));
                Debug.Assert(!match.IsMatch("Foo*bar!", '/', true));
            }
            {
                Glob match = new Glob(@"Foo\*bar*", STD_SEP_CHARS, true);   // mixed
                Debug.Assert(match.IsMatch("Foo*barge", '/', true));
                Debug.Assert(match.IsMatch("Foo*barf", '/', true));
                Debug.Assert(!match.IsMatch("Foo!barf", '/', true));
            }

            return true;
        }

        #endregion Unit tests
    }
}
