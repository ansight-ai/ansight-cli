# Native offline capture import

The host receives Swift/Kotlin recordings over an authenticated app WebSocket.
The app owns recording, configurable retention (default 10 sessions), and automatic
versus manual transfer. Redpoint owns the user-access decision. The host imports
archives and acknowledges only after imported evidence has been flushed to disk.

`offline_capture.begin` and `offline_capture.commit` carry the same descriptor:

```json
{
  "schema": "ansight.offline-transfer.v1",
  "sessionId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "transferId": "11111111222233334444555555555555",
  "byteCount": 12345,
  "sha256": "<64 lowercase hexadecimal characters>"
}
```

Between them the app sends ASFT chunk frames with sequential sequence numbers and
byte offsets, followed by a completion frame. The receiver accepts one archive per
connection, at most 1 GiB, with chunks up to 1 MiB. A retry begin cancels abandoned
work. Disconnect, invalid frames and completed imports remove the temporary file.
ZIP validation rejects unsafe/duplicate paths, more than 65,535 entries and over
2 GiB of uncompressed data. The archive session ID and app ID must match its
transfer descriptor and authenticated live connection respectively.

Import uses the existing JSONL archive reader for device/session metadata, custom
properties, metrics, events, logs, touches, network, screenshot indexes and visual
trees. Imported sessions use `offline-<source-session-id>`. Custom property group
`ansight.offlineCapture` persists the source ID and archive SHA-256. Matching retries
return the same imported session, including after host restart; content mismatches
are rejected. The successful commit response payload contains `sessionId`, `sha256`,
`importedSessionId`, and `durable: true`. The app deletes only after validating this
receipt. Binary delivery or a successful begin response is insufficient.

In manual mode, invoke the SDK's discovered `offline_capture.transfer` app tool.
It starts transfer without blocking the app's control receive pump. Existing hosts
that do not support this protocol reject begin and the SDK retains its captures.
This protocol is independent of hosted-cloud uploads and account authentication.

`OfflineCaptureTransferTests` exercises persistence/restart and repeat import,
all core evidence types, hash/source/app mismatch, incomplete/out-of-order frames,
unsafe archive paths, and disconnect cleanup. Run:

```sh
dotnet test tests/Ansight.Host.Tests/Ansight.Host.Tests.csproj --filter FullyQualifiedName~OfflineCaptureTransferTests
```
