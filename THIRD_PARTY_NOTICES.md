# Third-Party Notices

## Open Ephys bonsai-miniscope

Parts of the Open Ephys firmware UCLA Miniscope V4 and MiniCam support are adapted from the
[Open Ephys bonsai-miniscope](https://github.com/open-ephys/bonsai-miniscope)
project, reference commit `413dbe35ff0b31c9c4fa7b25ff6cf03d4e87f208`.

The adapted material includes the UVC extension-unit identity and control
layout, device USB identities, MediaCapture acquisition pattern, current DAQ
I2C command transport and initialization values, embedded YUY2 metadata layout,
and quaternion time-series visualizer pattern. This package modifies and
reorganizes those elements substantially, including its reactive API, direct
frame-callback processing, timestamps, frame models, reconnection, saving, and visualization.

Upstream copyright and license notice:

> Copyright (c) 2024 Open Ephys and Contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## Bonsai.FFmpeg VideoWriter

`UCLAMiniscope/VideoWriterFixed.cs` is adapted from the
[Bonsai.FFmpeg VideoWriter](https://github.com/bonsai-rx/ffmpeg/blob/v0.2.0/src/Bonsai.FFmpeg/VideoWriter.cs),
release `v0.2.0`, commit `ee403aeca3d6ecb214487a5c41d94b2f2b541526`.
The local version shortens the process-start delay and adds 16-bit grayscale
pixel-format support.

Upstream copyright and license notice:

> Copyright (c) Bonsai Foundation CIC and Contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.
