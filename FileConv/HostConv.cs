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

using CommonUtil;

namespace FileConv {
    /// <summary>
    /// Trivial class representing a file that the host should decode for itself, such as a
    /// GIF or JPEG.
    /// </summary>
    public class HostConv : IConvOutput {
        // IConvOutput
        public Notes Notes { get; } = new Notes();

        public enum FileKind {
            Unknown = 0, GIF, JPEG, PNG
        }

        public FileKind Kind { get; private set; }

        public HostConv(FileKind kind) {
            Kind = kind;
        }
    }
}
