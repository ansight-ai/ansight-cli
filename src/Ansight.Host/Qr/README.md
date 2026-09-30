# QR Attribution

This folder vendors QR generation and rendering code into Ansight.

Immediate source at import time:
- `Redpoint/Redpoint.Mobile/Qr`
- `Redpoint/Redpoint.Mobile/Qr/Image`
- `Redpoint/Redpoint.Mobile/Qr/Models`

Public upstream lineage:
- The Red-Point QR implementation matches the older `SkiaSharp.QrCode` API surface that exposed `QrCode`, `Vector2Slim`, `QRCodeData : IDisposable`, and `QRCodeRenderer : IDisposable`.
- Upstream repository: `https://github.com/guitarrapc/SkiaSharp.QrCode`
- Upstream package/readme notes that this older API existed before the `0.9.0` move to `QRCodeImageBuilder`.
- Upstream repository/package declares an MIT license.

Upstream acknowledgments carried forward here:
- `https://github.com/aloisdeniel/Xam.Forms.QRCode`
- `https://github.com/codebude/QRCoder`

The `SkiaSharp.QrCode` project specifically credits `codebude/QRCoder` for the QR generation algorithms. That attribution should remain attached to this vendored copy.

Ansight-specific changes in this folder:
- Namespaces were rewritten from `Redpoint.Mobile.Qr*` to `Ansight.Host.Qr*`.
- `QrCodePayload` was adapted from `Newtonsoft.Json` attributes to `System.Text.Json`.
- Nullable and build compatibility cleanups were applied for the Ansight solution.

If this folder is refreshed from Red-Point or upstream in the future, preserve this file and re-check upstream licensing and acknowledgments.
