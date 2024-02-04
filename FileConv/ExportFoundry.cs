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
using System.Reflection;
using System.Runtime.ExceptionServices;

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace FileConv {
    /// <summary>
    /// Export converter class instance creator.
    /// </summary>
    public static class ExportFoundry {
        /// <summary>
        /// List of supported export converters.
        /// </summary>
        /// <remarks>
        /// The order in which entries appear is not significant.
        /// </remarks>
        private static readonly ConverterEntry[] sConverters = {
            new ConverterEntry(typeof(Generic.HexDump)),
            new ConverterEntry(typeof(Generic.PlainText)),
            new ConverterEntry(typeof(Generic.Resources)),

            new ConverterEntry(typeof(Code.Applesoft)),
            new ConverterEntry(typeof(Code.BusinessBASIC)),
            new ConverterEntry(typeof(Code.IntegerBASIC)),
            new ConverterEntry(typeof(Code.ApplePascal_Code)),
            new ConverterEntry(typeof(Code.ApplePascal_Text)),
            new ConverterEntry(typeof(Code.LisaAsm)),
            new ConverterEntry(typeof(Code.MerlinAsm)),
            new ConverterEntry(typeof(Code.SCAsm)),
            new ConverterEntry(typeof(Code.Disasm65)),
            new ConverterEntry(typeof(Code.OMF)),

            new ConverterEntry(typeof(Doc.AppleWorksDB)),
            new ConverterEntry(typeof(Doc.AppleWorksSS)),
            new ConverterEntry(typeof(Doc.AppleWorksWP)),
            new ConverterEntry(typeof(Doc.AWGS_WP)),
            new ConverterEntry(typeof(Doc.CPMText)),
            new ConverterEntry(typeof(Doc.GutenbergWP)),
            new ConverterEntry(typeof(Doc.MagicWindow)),
            new ConverterEntry(typeof(Doc.RandomText)),
            new ConverterEntry(typeof(Doc.Teach)),

            new ConverterEntry(typeof(Gfx.HiRes)),
            new ConverterEntry(typeof(Gfx.HiRes_Font)),
            new ConverterEntry(typeof(Gfx.HiRes_LZ4FH)),
            new ConverterEntry(typeof(Gfx.DoubleHiRes)),
            new ConverterEntry(typeof(Gfx.SuperHiRes)),
            new ConverterEntry(typeof(Gfx.SuperHiRes_3201)),
            new ConverterEntry(typeof(Gfx.SuperHiRes_APF)),
            new ConverterEntry(typeof(Gfx.SuperHiRes_Brooks)),
            new ConverterEntry(typeof(Gfx.SuperHiRes_DreamGrafix)),
            new ConverterEntry(typeof(Gfx.SuperHiRes_Packed)),
            new ConverterEntry(typeof(Gfx.SuperHiRes_Paintworks)),
            new ConverterEntry(typeof(Gfx.PrintShopClip)),
            new ConverterEntry(typeof(Gfx.PrintShopFont)),
            new ConverterEntry(typeof(Gfx.HostImage)),
            new ConverterEntry(typeof(Gfx.MacPaint)),
        };

        private static readonly Dictionary<string, ConverterEntry> sTagList = GenerateTagList();
        private static Dictionary<string, ConverterEntry> GenerateTagList() {
            Dictionary<string, ConverterEntry> newList = new Dictionary<string, ConverterEntry>();
            foreach (ConverterEntry entry in sConverters) {
                newList.Add(entry.Tag, entry);
            }
            return newList;
        }

        private class ConverterEntry {
            // Tag from class TAG constant.
            public string Tag { get; private set; }
            public string Label { get; private set; }
            public string Description { get; private set; }
            public string Discriminator { get; private set; }
            public virtual List<Converter.OptionDefinition> OptionDefs { get; private set; }

            // Converter subclass.
            private Type mImplClass;

            // Cached reflection reference to constructor.
            private ConstructorInfo mCtorInfo;

            public ConverterEntry(Type implClass) {
                Debug.Assert(implClass.IsSubclassOf(typeof(Converter)));

                mImplClass = implClass;

                //Tag = (string)implClass.GetField("TAG")!.GetValue(null)!;
                //Label = (string)implClass.GetField("LABEL")!.GetValue(null)!;
                //Description = (string)implClass.GetField("DESCRIPTION")!.GetValue(null)!;

                ConstructorInfo? nullCtor = implClass.GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, Array.Empty<Type>());
                if (nullCtor == null) {
                    throw new Exception("Unable to find nullary ctor in " + implClass.FullName);
                }
                object instance = nullCtor.Invoke(Array.Empty<object>());
                Converter conv = (Converter)instance;
                Tag = conv.Tag;
                Label = conv.Label;
                Description = conv.Description;
                Discriminator = conv.Discriminator;
                OptionDefs = ((Converter)instance).OptionDefs;

                // Cache a reference to the constructor.
                ConstructorInfo? ctor = implClass.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance,
                    new Type[] { typeof(FileAttribs), typeof(Stream),
                        typeof(Stream), typeof(ResourceMgr), typeof(Converter.ConvFlags),
                        typeof(AppHook) }
                    );
                if (ctor == null) {
                    throw new Exception("Unable to find ctor in " + implClass.FullName);
                }
                mCtorInfo = ctor;
            }

            /// <summary>
            /// Creates a new instance of a Converter class.
            /// </summary>
            /// <param name="attrs">File attributes.</param>
            /// <param name="dataStream">Data fork stream; may be null.</param>
            /// <param name="rsrcStream">Rsrc fork stream; may be null.</param>
            /// <param name="resMgr">Resource manager, if non-empty resource fork present.</param>
            /// <param name="convFlags">Conversion flags.</param>
            /// <returns>New instance.</returns>
            public Converter CreateInstance(FileAttribs attrs, Stream? dataStream,
                    Stream? rsrcStream, ResourceMgr? resMgr, Converter.ConvFlags convFlags,
                    AppHook appHook) {
                object instance;
                try {
                    instance = mCtorInfo.Invoke(new object?[] {
                        attrs, dataStream, rsrcStream, resMgr, convFlags, appHook } );
                } catch (TargetInvocationException ex) {
                    if (ex.InnerException != null) {
                        // Re-throwing an inner exception loses the stack trace.  Do this instead.
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        // This is not reached, but the compiler doesn't know that.
                        throw ex.InnerException;
                    } else {
                        throw;
                    }
                }
                return (Converter)instance;
            }
        }

        /// <summary>
        /// Returns a sorted list of export converter tags.
        /// </summary>
        public static List<string> GetConverterTags() {
            List<string> keys = sTagList.Keys.ToList();
            keys.Sort();
            return keys;
        }

        /// <summary>
        /// Returns the number of known export converters.
        /// </summary>
        public static int GetCount() {
            return sConverters.Length;
        }

        /// <summary>
        /// Returns information for the Nth converter.
        /// </summary>
        /// <param name="index">Index of converter to query.</param>
        /// <param name="tag">Result: config file tag.</param>
        /// <param name="label">Result: UI label.</param>
        /// <param name="description">Result: long description.</param>
        public static void GetConverterInfo(int index, out string tag, out string label,
                out string description, out List<Converter.OptionDefinition> optionDefs) {
            ConverterEntry entry = sConverters[index];
            tag = entry.Tag;
            label = entry.Label;
            description = entry.Description + "\n\nApplies to: " + entry.Discriminator;
            optionDefs = entry.OptionDefs;
        }

        /// <summary>
        /// Creates an instance of the specified converter.
        /// </summary>
        /// <param name="tag">Converter tag.</param>
        /// <param name="attrs">File attributes.</param>
        /// <param name="dataStream">Data fork stream; may be null.</param>
        /// <param name="rsrcStream">Rsrc fork stream; may be null.</param>
        /// <returns>New instance, or null if the tag couldn't be found.</returns>
        public static Converter? GetConverter(string tag, FileAttribs attrs, Stream? dataStream,
                Stream? rsrcStream, AppHook appHook) {
            Debug.Assert(dataStream == null || dataStream.CanSeek);
            Debug.Assert(rsrcStream == null || rsrcStream.CanSeek);

            if (!sTagList.TryGetValue(tag, out ConverterEntry? convEntry)) {
                Debug.WriteLine("Converter tag '" + tag + "' not found");
                return null;
            }

            ResourceMgr? resMgr = null;
            if (rsrcStream != null && rsrcStream.Length > 0) {
                resMgr = ResourceMgr.Create(rsrcStream);
            }
            bool isRawDOS = (dataStream is DOS_FileDesc &&
                    ((DOS_FileDesc)dataStream).Part == FilePart.RawData);
            Converter.ConvFlags convFlags =
                isRawDOS ? Converter.ConvFlags.IsRawDOS : Converter.ConvFlags.None;

            return convEntry.CreateInstance(attrs, dataStream, rsrcStream, resMgr, convFlags,
                appHook);
        }

        /// <summary>
        /// Generates a list of converters that can be used with a file.  The list will be
        /// sorted from most-applicable to least.
        /// </summary>
        /// <remarks>
        /// This opens the streams in the IArchive or IFileSystem, handling MacZip automatically
        /// if configured to do so.
        /// </remarks>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem object.</param>
        /// <param name="fileEntry">File entry.</param>
        /// <param name="attrs">File attributes.</param>
        /// <param name="useRawMode">If true, open the data fork in "raw" mode.</param>
        /// <param name="enableMacZip">If true, this will look for resource forks in MacZip
        ///   ADF headers.</param>
        /// <param name="dataStream">Result: data fork file stream; may be null.</param>
        /// <param name="rsrcStream">Result: rsrc fork file stream; may be null.</param>
        /// <returns>List of applicable converter objects.</returns>
        /// <exception cref="IOException">Error occurred while opening files.</exception>
        public static List<Converter> GetApplicableConverters(object archiveOrFileSystem,
                IFileEntry fileEntry, FileAttribs attrs, bool useRawMode, bool enableMacZip,
                out Stream? dataStream, out Stream? rsrcStream, AppHook appHook) {
            // Create streams for data and resource forks.
            dataStream = null;
            rsrcStream = null;

            if (archiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)archiveOrFileSystem;
                if (fileEntry.IsDiskImage) {
                    // Normally we shouldn't be here, but this could happen if the main app
                    // couldn't recognize the disk image and kicked it over for a hex dump.
                    dataStream = ExtractToTemp(arc, fileEntry, FilePart.DiskImage);
                } else if (fileEntry.HasDataFork) {
                    dataStream = ExtractToTemp(arc, fileEntry, FilePart.DataFork);
                }
                if (fileEntry.HasRsrcFork && fileEntry.RsrcLength > 0) {
                    rsrcStream = ExtractToTemp(arc, fileEntry, FilePart.RsrcFork);
                } else if (enableMacZip && arc is Zip &&
                        Zip.HasMacZipHeader(arc, fileEntry, out IFileEntry adfEntry)) {
                    try {
                        // Open the AppleDouble header.
                        using ArcReadStream entryStream = arc.OpenPart(adfEntry, FilePart.DataFork);
                        Stream adfStream = TempFile.CopyToTemp(entryStream, adfEntry.DataLength);
                        using AppleSingle adfArc = AppleSingle.OpenArchive(adfStream, appHook);
                        IFileEntry adfArcEntry = adfArc.GetFirstEntry();
                        // Get file attributes.  If it has a resource fork, extract that.
                        attrs.GetFromAppleSingle(adfArcEntry);
                        if (adfArcEntry.HasRsrcFork && adfArcEntry.RsrcLength > 0) {
                            rsrcStream = ExtractToTemp(adfArc, adfArcEntry, FilePart.RsrcFork);
                        }
                    } catch (Exception ex) {
                        // Never mind.
                        Debug.WriteLine("Failed opening ADF header: " + ex.Message);
                        Debug.Assert(rsrcStream == null);
                    }
                }
            } else {
                IFileSystem fs = (IFileSystem)archiveOrFileSystem;
                if (fileEntry.HasDataFork) {
                    FilePart part = useRawMode ? FilePart.RawData : FilePart.DataFork;
                    dataStream = fs.OpenFile(fileEntry, FileAccessMode.ReadOnly, part);
                }
                if (fileEntry.HasRsrcFork && fileEntry.RsrcLength > 0) {
                    rsrcStream = fs.OpenFile(fileEntry, FileAccessMode.ReadOnly, FilePart.RsrcFork);
                }
            }

            return GetApplicableConverters(attrs, dataStream, rsrcStream, appHook);
        }

        /// <summary>
        /// Generates a list of converters that can be used with a file.  The list will be
        /// sorted from most-applicable to least.
        /// </summary>
        /// <param name="attrs">File attributes.</param>
        /// <param name="dataStream">Data fork file stream; may be null.</param>
        /// <param name="rsrcStream">Rsrc fork file stream; may be null.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>List of applicable converter objects.</returns>
        public static List<Converter> GetApplicableConverters(FileAttribs attrs,
                Stream? dataStream, Stream? rsrcStream, AppHook appHook) {
            Debug.Assert(dataStream == null || dataStream.CanSeek);
            Debug.Assert(rsrcStream == null || rsrcStream.CanSeek);

            List<Converter> converters = new List<Converter>();

            ResourceMgr? resMgr = null;
            if (rsrcStream != null && rsrcStream.Length > 0) {
                resMgr = ResourceMgr.Create(rsrcStream);
            }
            bool isRawDOS = (dataStream is DOS_FileDesc &&
                    ((DOS_FileDesc)dataStream).Part == FilePart.RawData);
            Converter.ConvFlags convFlags =
                isRawDOS ? Converter.ConvFlags.IsRawDOS : Converter.ConvFlags.None;

            foreach (ConverterEntry conv in sConverters) {
                Converter instance =
                    conv.CreateInstance(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook);
                Converter.Applicability applic = instance.Applic;

                if (applic > Converter.Applicability.Not) {
                    converters.Add(instance);
                }
            }

            // Sort results based on applicability.
            converters.Sort(delegate (Converter ent1, Converter ent2) {
                if (ent1.Applic > ent2.Applic) {
                    return -1;
                } else if (ent1.Applic < ent2.Applic) {
                    return 1;
                } else {
                    return 0;
                }
            });

            // TODO: may want to let PlainText peek at it to see if it wants to reject it.

            return converters;
        }

        /// <summary>
        /// Extracts an IArchive file entry to a temporary stream, in memory or on disk.  This
        /// is useful for converters because they need to be able to seek in the stream.
        /// </summary>
        /// <param name="archive">Archive that holds entry.</param>
        /// <param name="entry">Entry to extract.</param>
        /// <param name="part">File part to extract.</param>
        /// <returns>Temporary stream.</returns>
        private static Stream ExtractToTemp(IArchive archive, IFileEntry entry, FilePart part) {
            using ArcReadStream entryStream = archive.OpenPart(entry, part);

            // Get the length of the part we're extracting.  For gzip we need to do the part
            // query, as the DataLength field isn't currently set.  The part query may understate
            // the size of the output and should not be used to size a buffer.
            entry.GetPartInfo(part, out long partLength, out long unused1,
                out CompressionFormat unused2);

            return TempFile.CopyToTemp(entryStream, partLength);
        }
    }
}
