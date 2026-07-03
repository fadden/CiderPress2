/*
 * Copyright 2019 faddenSoft
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
using System;
using System.Globalization;

using Avalonia.Data.Converters;

namespace cp2_avalonia.Common {
    /// <summary>
    /// Converter that returns the boolean inverse of the input value.
    /// </summary>
    /// <remarks>
    /// Adapted from cp2_wpf/WPFCommon/InverseBooleanConverter.cs. Relaxed vs. the WPF
    /// original: does not throw on non-bool targets (Avalonia may pass typeof(object)),
    /// and ConvertBack is implemented symmetrically.
    /// </remarks>
    public class InverseBooleanConverter : IValueConverter {
        public object Convert(object? value, Type targetType, object? parameter,
                CultureInfo culture) {
            if (value is bool boolVal) {
                return !boolVal;
            }
            return value!;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter,
                CultureInfo culture) {
            if (value is bool boolVal) {
                return !boolVal;
            }
            return value!;
        }
    }
}
