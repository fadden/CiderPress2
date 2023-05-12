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
    public static class DAAppHook {
        //
        // Option name constants.  These are simple strings, but we want to use constants
        // so the compiler will catch typographical errors.
        //

        // Make "marked but unused" allocations a warning, rather than just an informational
        // message.  This is primarily for the benefit of the DiskArc test suite.
        public const string WARN_MARKED_BUT_UNUSED = "warn-marked-but-unused";

        // When scanning a DOS catalog, stop at the first unused entry.
        public const string DOS_STOP_FIRST_UNUSED = "dos-stop-first-unused";

        // Root directory when running library tests.  This is where "TestData" can be found.
        public const string LIB_TEST_ROOT = "lib-test-root";
    }
}
