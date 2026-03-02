# UCLA Miniscope Bonsai Integration

This repository contains Bonsai nodes and helper code for capturing and recording video and metadata from UCLA Miniscope and MiniCam devices.

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
This project is distributed under the MIT License — see `LICENSE`.

Notes
- The repository contains several helper services in `UCLAMiniscope/Helpers` used across nodes.
- If you publish binaries that include native libs, make sure to respect their licenses.

Maintainers
- Clément Bourguignon

