# Third-party notices

LiteLMSharp redistributes, or is derived from, the following third-party components.

---

## LiteRT-LM (and LiteRT prebuilt accelerator libraries)

- Source: https://github.com/google-ai-edge/LiteRT-LM
- Copyright: The ODML Authors / Google LLC
- License: Apache License 2.0 (same text as this repository's `LICENSE.txt`)

The native libraries shipped in the `LiteLMSharp.runtime.*` packages (`LiteRtLm` /
`libLiteRtLm` and companion accelerator libraries such as `LiteRt`,
`LiteRtWebGpuAccelerator`, `LiteRtTopKWebGpuSampler`, `LiteRtMetalAccelerator`,
`LiteRtTopKMetalSampler`, `LiteRtGpuAccelerator`, `LiteRtOpenClAccelerator`,
`GemmaModelConstraintProvider`) are built from LiteRT-LM source at a pinned release
tag, plus the prebuilt accelerator binaries that Google distributes in the same
repository under `prebuilt/<platform>/`.

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

## flutter_gemma (build recipe attribution)

- Source: https://github.com/DenisovAV/flutter_gemma
- License: MIT

`native/patch_c_api.sh` (the downstream patch that adds a shared-library Bazel target
for the LiteRT-LM C API) is derived from the approach pioneered by flutter_gemma's
`patch_c_api.sh`, along with several build/runtime insights documented in that project.

---

## Models

LiteLMSharp does not redistribute any model weights. Model files (e.g. Gemma
`.litertlm` bundles from https://huggingface.co/litert-community) are downloaded by
the end user and are subject to their own licenses and terms of use.
