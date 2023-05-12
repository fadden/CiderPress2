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

namespace CommonUtil {
    public static class Mirror {
        /// <summary>
        /// Finds all types that implement the specified type, using reflection.
        /// </summary>
        /// <remarks>
        /// <para>See discussion on https://stackoverflow.com/q/26733/294248.  There are
        /// various improvements possible, such as limiting the number of assemblies
        /// touched.  This is actually evaluating assignability, not implementation,
        /// which isn't quite the same thing.</para>
        /// </remarks>
        /// <param name="type">Type to search for.</param>
        /// <returns>List of types.</returns>
        public static IEnumerable<Type>? FindImplementingTypes(Type type) {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => type.IsAssignableFrom(p) && p.IsClass && !p.IsAbstract);
        }
    }
}
