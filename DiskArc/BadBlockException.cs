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

namespace DiskArc {
    /// <summary>
    /// Exception thrown when a bad block or sector is encountered.  This can only happen
    /// when working with nibble images or physical media.
    /// </summary>
    /// <remarks>
    /// This is a subclass of IOException, so that it can be handled generally with other
    /// I/O issues.
    /// </remarks>
    public class BadBlockException : IOException {
        public bool HasSectors { get; protected set; }
        public uint Block { get; protected set; }
        public uint Track { get; protected set; }
        public uint TrackFraction { get; protected set; }
        public uint Sector { get; protected set; }

        public BadBlockException() { }
        public BadBlockException(string message) : base(message) { }
        public BadBlockException(string message, Exception inner) : base(message, inner) { }

        /// <summary>
        /// Constructs the exception with a block value.
        /// </summary>
        public BadBlockException(string message, uint block)
                : base(message + " (Blk " + block + ")") {
            HasSectors = false;
            Block = block;
        }

        /// <summary>
        /// Constructs the exception with a track/sector.
        /// </summary>
        public BadBlockException(string message, uint trackNum, uint trackFraction,
                uint sectorNum, bool is525)
                : base(message + " (T" + trackNum + GetTrackFractionString(trackFraction, is525) +
                      " S" + sectorNum + ")") {
            HasSectors = true;
            Track = trackNum;
            TrackFraction = trackFraction;
            Sector = sectorNum;
        }

        private static string GetTrackFractionString(uint trackFraction, bool is525) {
            if (is525) {
                switch (trackFraction) {
                    case 0:
                        return string.Empty;
                    case 1:
                        return ".25";
                    case 2:
                        return ".5";
                    case 3:
                        return ".75";
                    default:
                        return ".??";
                }
            } else {
                switch (trackFraction) {
                    case 0:
                        return "/side 0";
                    case 1:
                        return "/side 1";
                    default:
                        return "/???";
                }
            }
        }
    }
}
