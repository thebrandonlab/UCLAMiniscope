# UCLA Miniscope Bonsai Integration

This repository contains Bonsai nodes and helper code for capturing and recording video and metadata from UCLA Miniscope and MiniCam devices.

Some components are adapted from the MIT-licensed Open Ephys `bonsai-miniscope`
and Bonsai.FFmpeg projects. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
for the exact source revisions, adapted areas, copyrights, and license notices.

Summary:
#TODO

Requirements
- Windows (captures use DirectShow)
- .NET Framework 4.7.2
- Visual Studio (recommended)
- Bonsai
- OpenCvSharp / OpenCV native libraries

Building
1. Restore NuGet packages in Visual Studio and build the solution.
2. Ensure native OpenCV/OpenCvSharp native dependencies are installed or available on PATH.

Usage
#TODO

License
This project is distributed under the [MIT License](LICENSE). Adapted source is
documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Notes
- The repository contains several helper services in `UCLAMiniscope/Helpers` used across nodes.
- If you publish binaries that include native libs, make sure to respect their licenses.

Maintainers
- Clément Bourguignon

