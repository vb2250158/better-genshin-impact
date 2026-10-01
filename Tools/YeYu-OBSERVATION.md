# YeYu passive observation bridge

This fork is based on official BetterGI 0.66.0, commit
`0af68c28f6b0e714d5304625ce644736d508022e`. The observer bridge version is
`0.66.0-yeyu.1` (assembly metadata `YeYuBridgeVersion`). BetterGI's official
Version and InformationalVersion remain unchanged, preserving its update and
cache semantics. This is a source-built fork, not an official release binary.

The existing GPL-3.0 license applies. Distribution must retain LICENSE and make
the corresponding fork source available. No executable patching is used.

The bridge is disabled unless both `BGI_YEYU_OBSERVATION_DIR` and
`BGI_YEYU_SCOPE_FILE` are supplied. The scope is UTF-8 JSON:

```json
{"schemaVersion":1,"gameId":"Genshin","runId":"00000000-0000-4000-8000-000000000001","runAttemptId":"00000000-0000-4000-8000-000000000002","accountId":"00000000-0000-4000-8000-000000000003","gameDay":"2000-01-01","taskOperations":{"official-task-id":"claim-mail","@check-rewards":"claim-daily-rewards"}}
```

`events.jsonl` and `upstream-log.jsonl` preserve scope and official task ID/name.
PNG artifacts are referenced by relative path and SHA256 in `frames/`.
Unknown method returns stay unknown. Explicit exception, cancellation and skip
branches are recorded without changing their original handling. Logger data is
retained through an added Serilog sink. Observer failures are fail-open.
An observed exception may be recovered by an official retry. It is therefore
an exception fact with `terminalConfirmed=false`, never a fabricated failure
terminal. Only an explicit skip branch is exported with a skipped outcome;
there is currently no success/failure terminal mapping.

Actual mouse dispatch boundaries collect independent BitBlt frames. A click
exports before, immediate after and a scheduled 100 ms frame, with real request
and capture timestamps, delay and later-input coverage. A drag exports only its
release frame. Delayed work is tracked and drained with a bounded exit wait.
Input arrays with multiple clicks (such as one SendInput double-click batch)
cannot expose intermediate physical boundaries and report missing evidence.
Side buttons and input outside the three instrumented APIs are not covered.

The account in scope is a declaration, never a verified in-game identity.
`accountIdentityConfirmed` remains false. The evidence scopes in the official
daily commission check export exact source pixels, text and boxes for every
OcrMatch call in that scope, including helper recognition. Their scope labels do
not prove those helper calls are reward completion. Other reward types lack
dedicated verification rules. No observation is a Manager reward-verification pass.
The observer sends no input and makes no game decisions or retries.

The separate probe never references BetterGI/App and does not enumerate game
windows, start capture, send input, or register protocols:

```powershell
dotnet publish Tools/BetterGI.YeYuProbe/BetterGI.YeYuProbe.csproj -c Release -r win-x64 --self-contained true
BetterGI.YeYuProbe.exe <installation-directory> <report.json>
```

It checks version, the installation's `yeyu-source-manifest.json`, required
resources, OpenCV encoding/decoding, capture-library construction/disposal, and
CPU ONNX model loading. A passing probe proves installation dependencies only.

Offline replay compiles the production observer source with an explicit capture
test double, without App or the real input dispatcher:

```powershell
dotnet build Tools/BetterGI.YeYuObservationReplay/BetterGI.YeYuObservationReplay.csproj -c Release
dotnet Tools/BetterGI.YeYuObservationReplay/bin/Release/net8.0-windows10.0.22621.0/BetterGI.YeYuObservationReplay.dll enabled <fresh-test-directory>
```

Run `off`, `invalid-scope`, `invalid-uuid`, `invalid-day`, and `write-failure` in separate fresh directories and
processes. Replay checks transport, fail-open behavior and capture sequencing.
Its generated images are synthetic and do not prove real-game capture accuracy.
