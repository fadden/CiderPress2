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
using System.Text;

namespace DiskArcTests {
    /// <summary>
    /// Static test patterns.
    /// </summary>
    public static class Patterns {
        /// <summary>
        /// 16KB simple ascending byte pattern (0 1 2 3 ...).
        /// </summary>
        public static readonly byte[] sBytePattern = GenerateBytePattern(16384);

        /// <summary>
        /// ~32KB simple ascending run pattern, 256 bytes of each.
        /// </summary>
        public static readonly byte[] sRunPattern = GenerateRunPattern(32000);

        /// <summary>
        /// Trivial text.  14 characters.
        /// </summary>
        public static readonly string sHelloWorld = "Hello, world!\r";
        public static readonly byte[] sHelloWorldBytes = Encoding.ASCII.GetBytes(sHelloWorld);

        /// <summary>
        /// Freidrich Nietzsche, _Beyond Good and Evil_, aphorism 146.  152 characters, no CR/LF.
        /// From <see href="https://books.google.com/books?id=yas8AAAAYAAJ"/>.
        /// </summary>
        public static readonly string sAbyss =
            "He who fights with monsters should be careful lest he thereby become a monster. " +
            "And if thou gaze long into an abyss, the abyss will also gaze into thee.";
        public static readonly byte[] sAbyssBytes = Encoding.ASCII.GetBytes(sAbyss);

        /// <summary>
        /// "Gettysburg Address", by Abraham Lincoln.  1474 characters.
        /// From <see href="https://www.abrahamlincolnonline.org/lincoln/speeches/gettysburg.htm"/>.
        /// </summary>
        public static readonly string sGettysburg =
            "Four score and seven years ago our fathers brought forth on this continent, " +
            "a new nation, conceived in Liberty, and dedicated to the proposition that all " +
            "men are created equal.\n" +
            "Now we are engaged in a great civil war, testing whether that nation, or any " +
            "nation so conceived and so dedicated, can long endure. We are met on a great " +
            "battle-field of that war. We have come to dedicate a portion of that field, as " +
            "a final resting place for those who here gave their lives that that nation might " +
            "live. It is altogether fitting and proper that we should do this.\n" +
            "But, in a larger sense, we can not dedicate -- we can not consecrate -- we can " +
            "not hallow -- this ground. The brave men, living and dead, who struggled here, " +
            "have consecrated it, far above our poor power to add or detract. The world will " +
            "little note, nor long remember what we say here, but it can never forget what they " +
            "did here. It is for us the living, rather, to be dedicated here to the unfinished " +
            "work which they who fought here have thus far so nobly advanced. It is rather for " +
            "us to be here dedicated to the great task remaining before us -- that from these " +
            "honored dead we take increased devotion to that cause for which they gave the " +
            "last full measure of devotion -- that we here highly resolve that these dead shall " +
            "not have died in vain -- that this nation, under God, shall have a new birth of " +
            "freedom -- and that government of the people, by the people, for the people, " +
            "shall not perish from the earth.\n";
        public static readonly byte[] sGettysburgBytes = Encoding.ASCII.GetBytes(sGettysburg);

        /// <summary>
        /// "Ulysses", by Alfred Lord Tennyson.  2987 characters.
        /// From <see href="https://www.poetryfoundation.org/poems/45392/ulysses"/>.
        /// </summary>
        public static readonly string sUlysses =
            "It little profits that an idle king,\n" +
            "By this still hearth, among these barren crags,\n" +
            "Match'd with an aged wife, I mete and dole\n" +
            "Unequal laws unto a savage race,\n" +
            "That hoard, and sleep, and feed, and know not me.\n" +
            "I cannot rest from travel: I will drink\n" +
            "Life to the lees: All times I have enjoy'd\n" +
            "Greatly, have suffer'd greatly, both with those\n" +
            "That loved me, and alone, on shore, and when\n" +
            "Thro' scudding drifts the rainy Hyades\n" +
            "Vext the dim sea: I am become a name;\n" +
            "For always roaming with a hungry heart\n" +
            "Much have I seen and known; cities of men\n" +
            "And manners, climates, councils, governments,\n" +
            "Myself not least, but honour'd of them all;\n" +
            "And drunk delight of battle with my peers,\n" +
            "Far on the ringing plains of windy Troy.\n" +
            "I am a part of all that I have met;\n" +
            "Yet all experience is an arch wherethro'\n" +
            "Gleams that untravell'd world whose margin fades\n" +
            "For ever and forever when I move.\n" +
            "How dull it is to pause, to make an end,\n" +
            "To rust unburnish'd, not to shine in use!\n" +
            "As tho' to breathe were life! Life piled on life\n" +
            "Were all too little, and of one to me\n" +
            "Little remains: but every hour is saved\n" +
            "From that eternal silence, something more,\n" +
            "A bringer of new things; and vile it were\n" +
            "For some three suns to store and hoard myself,\n" +
            "And this gray spirit yearning in desire\n" +
            "To follow knowledge like a sinking star,\n" +
            "Beyond the utmost bound of human thought.\n" +
            "\n" +
            "This is my son, mine own Telemachus,\n" +
            "To whom I leave the sceptre and the isle, --\n" +
            "Well-loved of me, discerning to fulfil\n" +
            "This labour, by slow prudence to make mild\n" +
            "A rugged people, and thro' soft degrees\n" +
            "Subdue them to the useful and the good.\n" +
            "Most blameless is he, centred in the sphere\n" +
            "Of common duties, decent not to fail\n" +
            "In offices of tenderness, and pay\n" +
            "Meet adoration to my household gods,\n" +
            "When I am gone. He works his work, I mine.\n" +
            "\n" +
            "There lies the port; the vessel puffs her sail:\n" +
            "There gloom the dark, broad seas. My mariners,\n" +
            "Souls that have toil'd, and wrought, and thought with me --\n" +
            "That ever with a frolic welcome took\n" +
            "The thunder and the sunshine, and opposed\n" +
            "Free hearts, free foreheads -- you and I are old;\n" +
            "Old age hath yet his honour and his toil;\n" +
            "Death closes all: but something ere the end,\n" +
            "Some work of noble note, may yet be done,\n" +
            "Not unbecoming men that strove with Gods.\n" +
            "The lights begin to twinkle from the rocks:\n" +
            "The long day wanes: the slow moon climbs: the deep\n" +
            "Moans round with many voices. Come, my friends,\n" +
            "'T is not too late to seek a newer world.\n" +
            "Push off, and sitting well in order smite\n" +
            "The sounding furrows; for my purpose holds\n" +
            "To sail beyond the sunset, and the baths\n" +
            "Of all the western stars, until I die.\n" +
            "It may be that the gulfs will wash us down:\n" +
            "It may be we shall touch the Happy Isles,\n" +
            "And see the great Achilles, whom we knew.\n" +
            "Tho' much is taken, much abides; and tho'\n" +
            "We are not now that strength which in old days\n" +
            "Moved earth and heaven, that which we are, we are;\n" +
            "One equal temper of heroic hearts,\n" +
            "Made weak by time and fate, but strong in will\n" +
            "To strive, to seek, to find, and not to yield.\n";
        public static readonly byte[] sUlyssesBytes = Encoding.ASCII.GetBytes(sUlysses);

        private static byte[] GenerateBytePattern(int count) {
            byte[] pattern = new byte[count];
            for (int i = 0; i < count; i++) {
                pattern[i] = (byte)i;
            }
            return pattern;
        }

        private static byte[] GenerateRunPattern(int count) {
            byte[] pattern = new byte[count];
            for (int i = 0; i < count; i++) {
                pattern[i] = (byte)(i >> 8);
            }
            return pattern;
        }

        /// <summary>
        /// Generates data that cannot be compressed by common algorithms.  All 256 byte values
        /// will be represented.
        /// </summary>
        /// <remarks>
        /// <para>This just gathers the output of a pseudo-random number generator.  The same
        /// seed is used every time.</para>
        /// </remarks>
        public static void GenerateUncompressible(byte[] buf, int offset, int count) {
            // Use a fixed seed so we get the same data set each time.
            Random rand = new Random(0);
            while (count-- != 0) {
                buf[offset++] = (byte)rand.Next(0, 256);
            }
        }

        /// <summary>
        /// Generates a pattern with an even distribution of byte values and infrequently-repeating
        /// patterns.  Mildly compressible by LZ.
        /// </summary>
        public static void GenerateTestPattern1(byte[] buf, int offset, int count) {
            int step = 1;
            while (count != 0) {
                int start = 0;
                int val = 0;
                int chunk = Math.Min(count, 256);
                for (int i = 0; i < chunk; i++) {
                    buf[offset++] = (byte)val;
                    val += step;
                    if (val > 255) {
                        val = ++start;
                    }
                }

                count -= chunk;
                step = (step % 255) + 1;        // [1,255]
            }
        }

        /// <summary>
        /// Generates a pattern of increasingly long runs.  Highly compressible.
        /// </summary>
        /// <param name="buf">Output buffer.</param>
        /// <param name="offset">Initial offset in output buffer.</param>
        /// <param name="count">Number of bytes to generate.</param>
        public static void GenerateTestPattern2(byte[] buf, int offset, int count) {
            int runLen = 1;
            while (count != 0) {
                int chunk = Math.Min(count, runLen);
                for (int i = 0; i < chunk; i++) {
                    buf[offset++] = (byte)runLen;
                }

                count -= chunk;
                runLen++;
            }
        }

        /// <summary>
        /// Copies a string to a buffer, converting it to ASCII bytes.
        /// </summary>
        /// <remarks>
        /// Similar to <see cref="Encoding.ASCII.GetBytes"/>, but with a user-supplied buffer.
        /// The output buffer must be large enough to hold the data.
        /// </remarks>
        /// <param name="str">String to convert.</param>
        /// <param name="buf">Output buffer.</param>
        /// <param name="offset">Initial offset in output buffer.</param>
        /// <returns>Number of bytes output.</returns>
        public static int StringToBytes(string str, byte[] buf, int offset) {
            for (int i = 0; i < str.Length; i++) {
                buf[offset + i] = (byte)str[i];
            }
            return str.Length;
        }
    }
}
