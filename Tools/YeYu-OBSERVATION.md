# YeYu official observation bridge v2

Official BetterGI 0.66.0 base: `0af68c28f6b0e714d5304625ce644736d508022e`.
Optional bridge assembly metadata: `YeYuBridgeVersion=0.66.0-yeyu.2`. The official
Version and InformationalVersion remain 0.66.0. This is a source-built fork under
the existing GPL-3.0 license; retain LICENSE and corresponding fork source.

The [scope and event contract](yeyu-observation-v2.md) describes explicit opt-in,
UID confirmation, cancellation and recognition artifacts. V2 removes the V1
mouse-boundary captures and all delayed capture work. V1 artifacts retain their
historical action-image semantics; they are never relabeled as recognition input.
PostMessageSimulator, MouseEventSimulator and WindowsInputMessageDispatcher are
restored to the official source. Normal offline/manual use installs no observer.

Transport failures are fail-open. The official native input, task order, return
values, exception objects, retries and resource decisions remain intact. Requested
invalid scopes and mismatching/unreadable account UID refuse game task execution
at the independent authorization boundary. Named accounts never discover an
arbitrary current UID. No credential, account switch, extra page navigation or
reward selection is added.

ImageRegion OCR/template and direct Paddle OCR export actual inputs and results.
OCR source ROI is separate from boxes in the archived pixel space. Frame capture
time is unknown and remains null. Sampling is bounded (two ordinary plus six
dedicated images per operation); image/log write failures do not fail game tasks.
New read-only postcondition reports use a single official capture/OCR at the page
the official task still has open: no extra input, wait, retry or page opening.

Explicit official count/resource/exception/skip branches export terminals.
Plain return, `DragonEnd.Success`, reward button clicks and ambiguous
"unfinished or already claimed" branches never become completed terminals.
Exceptions inside official retries remain diagnostic facts; only exhausted or
propagated task exception boundaries are failures.

The daily reward composite has distinct encounter points, Catherine, expeditions,
battle-pass points and battle-pass rewards facts. The final commission status
checks only commission/Catherine status. It cannot prove all composite rewards.
Only all six required source facts together can emit a late operation_terminal;
that report retains the original account, RunAttempt, game day and Todo attempt.
Postcondition anchors are candidate reporting rules; Manager must independently
re-read the same scoped image/hash using a real recognizer and must validate each
page rule with real positive/negative samples before acceptance. Synthetic replay
or image presence does not prove game completion.

The standalone installation probe never references BetterGI/App, starts capture,
sends input or registers a protocol. Its dependency success proves installation,
not game success. The standalone config exporter references the official model
and shared original eight-task initializer without instantiating App, Host or a
viewmodel. It uses PascalCase official JSON and FileMode.CreateNew:

```powershell
BetterGI.YeYuConfigExport.exe --name YeYu-Genshin --output C:/absolute/new-config.json
```

Offline replay links production YeYuObservation.cs with explicit recognition
result doubles. There is no fake capture class, game App or native dispatcher.
Scenarios: off, invalid-scope, invalid-uuid, invalid-day, named-without-uid,
write-failure, enabled, mismatch and composite. It validates exact supplied pixel copies,
hashes, coordinate space, bounded polling, unknown returns, recovered exceptions,
explicit terminals, UID discovery/mismatch, cancellation, and no action images.
Generated synthetic images never count as real-game evidence.
