# Third-party notices

StorageInventory's own source code is licensed under the [MIT License](LICENSE). The source code references no
third-party packages.

## Redistributed in the release executable

The release `StorageInventory.exe` is a **self-contained** .NET application: it bundles the .NET runtime and the
Windows Desktop (WPF) runtime so that no separate .NET installation is needed. Both come from Microsoft's official
runtime packs on nuget.org:

| Component | Version | Licence |
|---|---|---|
| .NET runtime (`Microsoft.NETCore.App.Runtime.win-x64`) | 10.0.12 | MIT |
| Windows Desktop runtime, including WPF (`Microsoft.WindowsDesktop.App.Runtime.win-x64`) | 10.0.12 | MIT |

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

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

The .NET runtime itself incorporates third-party components. Their notices are published by the .NET project:

- .NET runtime: <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT> (also shipped as
  `THIRD-PARTY-NOTICES.TXT` inside the `Microsoft.NETCore.App.Runtime.win-x64` 10.0.12 package)
- WPF: <https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT>

The application uses the Windows system fonts Segoe UI and Segoe Fluent Icons; no fonts, icons or images are bundled.

## Development and test tools (not redistributed)

`tools\fetch-tools.ps1` downloads these into the git-ignored `tools\` folder for building and testing. They are not
part of the source tree or of the release executable.

| Tool | Used for | Licence |
|---|---|---|
| .NET SDK 10.0.401 | Building | MIT (with third-party components; see the SDK's own notices) |
| PowerShell 7.6.6 | Test fixtures, and the PowerShell reference implementation as a parity oracle | MIT |
| ImportExcel 7.8.10 (bundles EPPlus 4.5) | Tests only: an independent reader for the optional workbook | Apache-2.0 (ImportExcel), LGPL-2.1 (EPPlus 4.5) |
