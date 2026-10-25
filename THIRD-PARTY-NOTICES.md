# Third-party notices

MDCL is independently implemented. No PCL or HMCL source code, icons, or assets have been copied into this project.

| Component | Version | Purpose | License / source |
| --- | --- | --- | --- |
| .NET SDK/runtime | 10.0.402 / 10.0.13 | Application platform | MIT; https://github.com/dotnet/runtime |
| Avalonia and listed Avalonia packages | 12.1.4 | Cross-platform desktop UI | MIT; https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM primitives | MIT; https://github.com/CommunityToolkit/dotnet |
| Microsoft.Data.Sqlite | 10.0.13 | Local index and journal storage | MIT; https://github.com/dotnet/efcore |
| SQLite (through SQLitePCLRaw) | locked transitively | Embedded database | Public domain; https://www.sqlite.org/copyright.html |
| Microsoft.Extensions.* | 10.0.13 | DI and logging | MIT; https://github.com/dotnet/runtime |
| System.Security.Cryptography.ProtectedData | 10.0.13 | Windows DPAPI adapter | MIT; https://github.com/dotnet/runtime |
| xUnit and test runner | 2.9.3 / 4.0.1 | Tests (not shipped) | Apache-2.0 / MIT; https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | 18.11.0 | Test host (not shipped) | MIT; https://github.com/microsoft/vstest |

The authoritative transitive versions and hashes are in each project's `packages.lock.json`. A complete release dependency report is generated during T08. No third-party code or resource may be added without updating this file and reviewing its license.
