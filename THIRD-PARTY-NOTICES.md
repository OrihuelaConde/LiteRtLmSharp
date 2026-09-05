# Third-party notices

LiteRtLmSharp redistributes, or is derived from, the following third-party components.

---

## LiteRT-LM (Google's official C API prebuilts)

- Source: https://github.com/google-ai-edge/LiteRT-LM
- Copyright: The ODML Authors / Google LLC
- License: Apache License 2.0 (same text as this repository's `LICENSE.txt`)

The native libraries shipped in the `LiteRtLmSharp.runtime.*` packages are Google's official
LiteRT-LM C API prebuilts (`litert_lm_c_api-<version>.zip` and `CLiteRTLM.xcframework.zip`
from the pinned upstream release), redistributed unmodified apart from the file name
(`LiteRtLm.dll`, `libLiteRtLm.so`, `libLiteRtLm.dylib`). Each library embeds the LiteRT
runtime, the GPU accelerators and samplers, the constraint provider and LlGuidance, among
other components; upstream's notice file covering those binaries ships in every runtime
package as `THIRD_PARTY_NOTICES.litert-lm.txt`.

LiteRT, LiteRT-LM and Gemma are trademarks of Google LLC. This project is not
affiliated with, sponsored, or endorsed by Google.

---

## DirectX Shader Compiler runtime (`dxcompiler.dll`, `dxil.dll`) — win-x64 only

- Source: https://github.com/microsoft/DirectXShaderCompiler
- License: LLVM Release License (University of Illinois/NCSA Open Source License)

```
University of Illinois/NCSA
Open Source License

Copyright (c) 2003-2015 University of Illinois at Urbana-Champaign.
All rights reserved.

Developed by:

    LLVM Team

    University of Illinois at Urbana-Champaign

    http://llvm.org

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal with
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

    * Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimers.

    * Redistributions in binary form must reproduce the above copyright notice,
      this list of conditions and the following disclaimers in the
      documentation and/or other materials provided with the distribution.

    * Neither the names of the LLVM Team, University of Illinois at
      Urbana-Champaign, nor the names of its contributors may be used to
      endorse or promote products derived from this Software without specific
      prior written permission.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
CONTRIBUTORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS WITH THE
SOFTWARE.
```

Full license (including additional components):
https://github.com/microsoft/DirectXShaderCompiler/blob/main/LICENSE.TXT

---

## flutter_gemma (attribution)

- Source: https://github.com/DenisovAV/flutter_gemma
- License: MIT

Several build and runtime insights documented in this repository (the Android
`<uses-native-library>` requirement, the DirectX Shader Compiler runtime on Windows, the
GPU sampler issues) were first worked out in flutter_gemma. Until LiteRT-LM v0.15.0 the
natives were built with a downstream Bazel patch derived from its `patch_c_api.sh` (see git
history); since v0.16.0 no code from it remains.

---

## Models

LiteRtLmSharp does not redistribute any model weights. Model files (e.g. Gemma
`.litertlm` bundles from https://huggingface.co/litert-community) are downloaded by
the end user and are subject to their own licenses and terms of use.
