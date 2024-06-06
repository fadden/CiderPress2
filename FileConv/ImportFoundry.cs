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

namespace FileConv {
    /// <summary>
    /// Import convert class instance creator.
    /// </summary>
    public static class ImportFoundry {
        /// <summary>
        /// List of supported import converters.
        /// </summary>
        private static readonly ConverterEntry[] sConverters = {
            new ConverterEntry(typeof(Generic.PlainTextImport)),

            new ConverterEntry(typeof(Code.ApplesoftImport)),
            new ConverterEntry(typeof(Code.MerlinAsmImport)),
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
            public List<Converter.OptionDefinition> OptionDefs { get; private set; }

            // Converter subclass.
            private Type mImplClass;

            // Cached reflection reference to constructor.
            private ConstructorInfo mCtorInfo;

            public ConverterEntry(Type implClass) {
                Debug.Assert(implClass.IsSubclassOf(typeof(Importer)));

                mImplClass = implClass;

                ConstructorInfo? nullCtor = implClass.GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, Array.Empty<Type>());
                if (nullCtor == null) {
                    throw new Exception("Unable to find nullary ctor in " + implClass.FullName);
                }
                object instance = nullCtor.Invoke(Array.Empty<object>());
                Importer imp = (Importer)instance;
                Tag = imp.Tag;
                Label = imp.Label;
                Description = imp.Description;
                Discriminator = imp.Discriminator;
                OptionDefs = imp.OptionDefs;

                // Cache a reference to the constructor.
                ConstructorInfo? ctor = implClass.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, new Type[] { typeof(AppHook) });
                if (ctor == null) {
                    throw new Exception("Unable to find ctor in " + implClass.FullName);
                }
                mCtorInfo = ctor;
            }

            /// <summary>
            /// Creates a new instance of an Importer class.
            /// </summary>
            /// <returns>New instance.</returns>
            public Importer CreateInstance(AppHook appHook) {
                object instance;
                try {
                    instance = mCtorInfo.Invoke(new object?[] { appHook });
                } catch (TargetInvocationException ex) {
                    if (ex.InnerException != null) {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        throw ex.InnerException;
                    } else {
                        throw;
                    }
                }
                return (Importer)instance;
            }
        }

        /// <summary>
        /// Returns a sorted list of import converter tags.
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
        /// <returns>New instance, or null if the tag couldn't be found.</returns>
        public static Importer? GetConverter(string tag, AppHook appHook) {
            if (!sTagList.TryGetValue(tag, out ConverterEntry? convEntry)) {
                Debug.WriteLine("Converter tag '" + tag + "' not found");
                return null;
            }

            return convEntry.CreateInstance(appHook);
        }
    }
}
