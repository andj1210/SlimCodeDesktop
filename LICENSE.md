# Licenses

## SlimCode Desktop

Copyright (C) 2026 Andreas Jung

SlimCode Desktop is licensed under the **GNU General Public License v3.0 only**
(`GPL-3.0-only`). Full text: [LICENSE.app.md](LICENSE.app.md).

## Third-party components

The distributed application bundles the components below. All of them use permissive
licenses that are compatible with GPL-3.0. Each linked file holds the full license text.

| Component | Version | License | Shipped in | License file |
|---|---|---|---|---|
| Avalonia (UI framework, all `Avalonia.*` packages) | 12.1.5 | MIT | all builds | [LICENSE.avalonia.md](LICENSE.avalonia.md) |
| Inter typeface (embedded in `Avalonia.Fonts.Inter`) | 12.1.5 | OFL-1.1 | all builds | [LICENSE.inter.md](LICENSE.inter.md) |
| ANGLE (`Avalonia.Angle.Windows.Natives`) | 2.1.27548.20260419 | BSD-3-Clause | Windows builds | [LICENSE.angle.md](LICENSE.angle.md) |
| SkiaSharp, HarfBuzzSharp (managed bindings) | 4.154.0 / 8.3.1.3 | MIT | all builds | [LICENSE.skiasharp.md](LICENSE.skiasharp.md) |
| libSkiaSharp, libHarfBuzzSharp (native: Skia, HarfBuzz, FreeType, libpng, ...) | as above | BSD-3-Clause, Old MIT, others | all builds | [LICENSE.skiasharp-native.md](LICENSE.skiasharp-native.md) |
| Markdig | 0.41.3 | BSD-2-Clause | all builds | [LICENSE.markdig.md](LICENSE.markdig.md) |
| PdfPig (`UglyToad.PdfPig.*`) | 0.1.16 | Apache-2.0 | all builds | [LICENSE.pdfpig.md](LICENSE.pdfpig.md) |
| PDFtoImage | 5.4.0 | MIT | all builds | [LICENSE.pdftoimage.md](LICENSE.pdftoimage.md) |
| PDFium (native, `bblanchon.PDFium.*`) | 152.0.7961 | BSD-3-Clause, Apache-2.0, others | all builds | [LICENSE.pdfium.md](LICENSE.pdfium.md) |
| MicroCom.Runtime | 0.11.6 | MIT | all builds | [LICENSE.microcom.md](LICENSE.microcom.md) |
| Tmds.DBus.Protocol | 0.94.1 | MIT | all builds (used on Linux) | [LICENSE.tmds-dbus.md](LICENSE.tmds-dbus.md) |
| .NET Runtime and app host | 10.0.10 | MIT | runtime: self-contained builds; host: all builds | [LICENSE.dotnet.md](LICENSE.dotnet.md) |
| .NET Runtime third-party code | 10.0.10 | various | self-contained builds | [LICENSE.dotnet-third-party.md](LICENSE.dotnet-third-party.md) |

### Not distributed

These appear in the dependency graph but do not ship in release builds:

- `AvaloniaUI.DiagnosticsSupport`: Debug builds only. Its assets are excluded in
  Release (see `SlimCodeDesktop.csproj`).
- `Avalonia.BuildServices`: build-time only (MIT).
- `Microsoft.Extensions.*`, `Microsoft.IO.RecyclableMemoryStream`: pulled in only
  by the diagnostics package (MIT).

## Updating

When a package is added or its version changes, update the table above and the
matching `LICENSE.<component>.md`. The resolved package list is in
`obj/project.assets.json`. Most license texts come from the package folder under
`packages/` or from the upstream repository.
