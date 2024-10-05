/*
 * Copyright 2024 faddenSoft
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

namespace CommonUtil {
    /// <summary>
    /// This processes a RIFF audio file (.wav).
    /// </summary>
    /// <remarks>
    /// <para>Thanks: https://stackoverflow.com/q/8754111/294248 ,
    /// http://soundfile.sapp.org/doc/WaveFormat/ , and
    /// https://www.mmsp.ece.mcgill.ca/Documents/AudioFormats/WAVE/Docs/riffmci.pdf .</para>
    /// </remarks>
    public class WAVFile {
        // Chunk signatures.
        public static readonly int SIG_RIFF = RawData.IntifyASCII("RIFF");
        public static readonly int SIG_WAVE = RawData.IntifyASCII("WAVE");
        public static readonly int SIG_FMT  = RawData.IntifyASCII("fmt ");
        public static readonly int SIG_DATA = RawData.IntifyASCII("data");

        // Selected constants from RFC 2361.
        public const int WAVE_FORMAT_UNKNOWN = 0x0000;
        public const int WAVE_FORMAT_PCM = 0x0001;
        public const int WAVE_FORMAT_ADPCM = 0x0002;
        public const int WAVE_FORMAT_IEEE_FLOAT = 0x0003;

        private const int MIN_LEN = 44;
        private const int RIFF_HEADER_LEN = 12;
        private const int CHUNK1_MIN_LEN = 16;
        private const int CHUNK1_MAX_LEN = 128;     // arbitrary

        /// <summary>
        /// Format tag.  Defines the format of the audio samples.  PCM is format 1.
        /// </summary>
        public int FormatTag { get; private set; }

        /// <summary>
        /// Number of channels.  1 for mono, 2 for stereo, etc.
        /// </summary>
        public int Channels { get; private set; }

        /// <summary>
        /// Number of samples per second.  Typically 8000, 22050, or 44100.
        /// </summary>
        public int SamplesPerSec { get; private set; }

        /// <summary>
        /// Average number of bytes per second.  For PCM this is exact, for compressed formats
        /// it isn't.
        /// </summary>
        public int AvgBytesPerSec { get; private set; }

        /// <summary>
        /// Block alignment, in bytes, of the waveform data.  This can be used by playback
        /// software when allocating buffers.
        /// </summary>
        public int BlockAlign { get; private set; }

        /// <summary>
        /// For PCM: number of bits per sample, usually 8 or 16.
        /// </summary>
        public int BitsPerSample { get; private set; }

        /// <summary>
        /// Offset of start of sample data.
        /// </summary>
        public long DataOffset { get; private set; }

        /// <summary>
        /// Length of sample data, in bytes.
        /// </summary>
        public int DataLength { get; private set; }

        //public int BytesPerSample {
        //    get { return ((BitsPerSample + 7) / 8) * Channels; }
        //}


        /// <summary>
        /// Private constructor.
        /// </summary>
        private WAVFile() { }

        /// <summary>
        /// Parses the WAV file header.  Does not process the audio samples.
        /// </summary>
        /// <param name="stream">WAV file data stream, positioned at start.</param>
        /// <returns>A new object with properties set, or null on failure.</returns>
        public static WAVFile? ReadHeader(Stream stream) {
            WAVFile wav = new WAVFile();
            if (!wav.ParseHeader(stream)) {
                return null;
            }
            if (wav.FormatTag != WAVE_FORMAT_PCM ||
                    (wav.BitsPerSample != 8 && wav.BitsPerSample != 16 &&
                        wav.BitsPerSample != 32)) {
                // We don't know how to process this type of file.
                return null;
            }
            return wav;
        }

        /// <summary>
        /// Parses the headers out of the RIFF file.  On return, the stream will be positioned
        /// at the start of the audio sample data.
        /// </summary>
        /// <param name="stream">Data stream, positioned at start of RIFF data.</param>
        /// <returns>True on success.</returns>
        private bool ParseHeader(Stream stream) {
            if (stream.Length - stream.Position < MIN_LEN) {
                return false;
            }

            // Read the RIFF header.
            byte[] riffHeader = new byte[RIFF_HEADER_LEN];
            stream.ReadExactly(riffHeader, 0, RIFF_HEADER_LEN);
            uint chunkId = RawData.GetU32BE(riffHeader, 0);
            if (chunkId != SIG_RIFF) {
                Debug.WriteLine("Not a RIFF (.wav) file, stream now at " + stream.Position);
                return false;
            }
            uint chunkSize = RawData.GetU32LE(riffHeader, 4);   // size of everything that follows
            if (stream.Length - stream.Position + 4 < chunkSize) {
                Debug.WriteLine("WAV file is too short");
                return false;
            }
            uint chunkFormat = RawData.GetU32BE(riffHeader, 8);
            if (chunkId != SIG_RIFF || chunkFormat != SIG_WAVE) {
                Debug.WriteLine("Incorrect WAVE file header, stream now at " + stream.Position);
                return false;
            }

            // Read the sub-chunk #1 header and data.  We expect the "fmt " chunk to come first.
            // We don't know exactly how large it will be, but it's safe to assume that anything
            // we understand will have a reasonably-sized chunk here.
            bool ok;
            uint subChunk1Id = RawData.ReadU32BE(stream, out ok);
            uint subChunk1Size = RawData.ReadU32LE(stream, out ok);
            if (subChunk1Id != SIG_FMT ||
                    subChunk1Size < CHUNK1_MIN_LEN || subChunk1Size > CHUNK1_MAX_LEN) {
                Debug.WriteLine("Bad subchunk1 header");
                return false;
            }
            byte[] subChunk1Header = new byte[subChunk1Size];
            stream.ReadExactly(subChunk1Header, 0, (int)subChunk1Size);

            // Process the common fields.
            FormatTag = RawData.GetU16LE(subChunk1Header, 0);
            Channels = RawData.GetU16LE(subChunk1Header, 2);
            SamplesPerSec = (int)RawData.GetU32LE(subChunk1Header, 4);
            AvgBytesPerSec = (int)RawData.GetU32LE(subChunk1Header, 8);
            BlockAlign = RawData.GetU16LE(subChunk1Header, 12);

            if (SamplesPerSec <= 0) {
                Debug.WriteLine("Invalid sample rate " + SamplesPerSec);
                return false;
            }
            if (Channels == 0) {
                Debug.WriteLine("Invalid NumChannels " + Channels);
                return false;
            }

            // Process the format-specific fields.
            if (FormatTag == WAVE_FORMAT_PCM) {
                BitsPerSample = RawData.GetU16LE(subChunk1Header, 14);
                if (BitsPerSample == 0 || BitsPerSample > 256) {
                    Debug.WriteLine("Invalid bits per sample " + BitsPerSample);
                    return false;
                }
                // These equations are in the PCM-specific section of the Microsoft spec.
                if (AvgBytesPerSec != SamplesPerSec * Channels * BitsPerSample / 8) {
                    Debug.WriteLine("Warning: ByteRate has unexpected value " + AvgBytesPerSec);
                }
                if (BlockAlign != Channels * BitsPerSample / 8) {
                    Debug.WriteLine("Warning: BlockAlign has unexpected value " + BlockAlign);
                }
            } else {
                Debug.WriteLine("Warning: audio format is not PCM: " + FormatTag);
            }

            // Find the "data" chunk.  We may encounter a "fact" chunk first; ignore it.  Keep
            // scanning until we find it or run out of file.
            uint subChunk2Id, subChunk2Size;
            while (true) {
                subChunk2Id = RawData.ReadU32BE(stream, out ok);
                subChunk2Size = RawData.ReadU32LE(stream, out ok);
                if (!ok || subChunk2Size == 0) {
                    Debug.WriteLine("Unable to find data chunk");
                    return false;
                }
                if (subChunk2Id == SIG_DATA) {
                    break;
                }
                Debug.WriteLine("Skipping chunk: '" +
                    RawData.StringifyU32BE(subChunk2Id) + "'");
                stream.Seek(subChunk2Size, SeekOrigin.Current);
            }
            if (stream.Length - stream.Position < subChunk2Size) {
                Debug.WriteLine("Bad subchunk2size " + subChunk2Size);
                return false;
            }

            // All done.  Stream is positioned at the start of the data.
            DataOffset = stream.Position;
            DataLength = (int)subChunk2Size;
            return true;
        }

        //
        // WAVE PCM encoding, briefly:
        //
        // Samples are stored sequentially, in whole bytes.  For sample sizes that aren't a
        // multiple of 8, data is stored the most-significant bits.  The low bits are set to zero.
        //
        // For bits per sample <= 8, values are stored as unsigned, e.g. 8 bits = [0,255].
        // For bits per sample > 8, values are stored as signed, e.g. 16 bits = [-32768,32767].
        //
        // Data packing for PCM WAVE files:
        //  8-bit mono: (ch0) (ch0) (ch0) (ch0) ...
        //  8-bit stereo: (ch0 ch1) (ch0 ch1) ...
        //  16-bit mono: (ch0l ch0h) (ch0l ch0h) ...
        //  16-bit stereo: (ch0l ch0h ch1l ch1h) ...
        //

        public int ConvertSamplesToReal(Stream stream, int count, float[] outBuf) {
            // TODO
            // For stereo recordings, just use the left channel, which comes first in
            // each sample.
            // https://github.com/fadden/ciderpress/blob/fc2fc1429df0a099692d9393d214bd6010062b1a/app/CassetteDialog.cpp#L715
            throw new NotImplementedException();
        }
    }
}
