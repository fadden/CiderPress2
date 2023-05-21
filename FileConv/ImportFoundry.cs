﻿/*
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

            // Converter subclass.
            private Type mImplClass;

            // Cached reflection reference to constructor.
            private ConstructorInfo mCtorInfo;

            public ConverterEntry(Type implClass) {
                Debug.Assert(implClass.IsSubclassOf(typeof(Importer)));

                mImplClass = implClass;

                Tag = (string)implClass.GetField("TAG")!.GetValue(null)!;

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
                        throw ex.InnerException;
                    } else {
                        throw ex;
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