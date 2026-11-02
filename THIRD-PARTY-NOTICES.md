# Third-party notices

`local_packages/` carries patched builds of other projects' packages (`patches/README.md`). Each is redistributed under its own licence, given below. The lab's own packages are under this repository's `LICENSE`.

## OpenRigs.Core, OpenRigs.Devices, OpenRigs.Logging, OpenRigs.Streaming, OpenRigs.Video

`local_packages/OpenRigs.*.0.1.0-bbox.1.nupkg` are built from [open-rigs](https://github.com/open-rigs/open-rigs) at `3788bfbb0946bd5a8e5ade321da9815cc1ff1c07` with the two camera patches in `patches/`. They are redistributed under the open-rigs BSD 3-Clause License:

```
BSD 3-Clause License

Copyright 2026 (c) University College London and Contributors

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Bonsai.FFmpeg

`local_packages/Bonsai.FFmpeg.0.2.0-uniquepipe.nupkg` is a modified copy of [Bonsai.FFmpeg](https://github.com/bonsai-rx/ffmpeg) 0.2.0 (commit `ee403aeca3d6ecb214487a5c41d94b2f2b541526`). The change gives each recording its own named pipe, so starting a recording while the previous one is still being encoded no longer fails with "All pipe instances are busy." The patch is `patches/bonsai-ffmpeg-unique-pipe.patch`. It is redistributed under the MIT License, carried in the package's `LICENSE` file:

```
Copyright (c) Bonsai Foundation CIC and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
