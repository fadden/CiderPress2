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

namespace FileConv {
    /// <summary>
    /// Exception thrown by file converters when the conversion process fails due to bad data.
    /// This is really just for importers; exporters are expected to cope with bad data.
    /// </summary>
    public class ConversionException : Exception {
        public ConversionException() { }
        public ConversionException(string message) : base(message) { }
        public ConversionException(string message, Exception inner) : base(message, inner) { }
    }
}
