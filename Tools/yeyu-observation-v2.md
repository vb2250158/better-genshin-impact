# YeYu observation v2

Opt-in environment: `BGI_YEYU_OBSERVATION_DIR`, `BGI_YEYU_SCOPE_FILE`.
Scope file (UTF-8 JSON, created by the runner before the process starts):

```json
{
  "schemaVersion": 2,
  "gameId": "Genshin",
  "runId": "00000000-0000-4000-8000-000000000001",
  "runAttemptId": "00000000-0000-4000-8000-000000000002",
  "accountId": "default",
  "gameDay": "2000-01-01",
  "expectedUid": null,
  "cancelFile": "C:/absolute/observation/cancel.request",
  "taskOperations": { "official-task-guid": "claim-mail" },
  "todoAttemptIds": { "claim-mail": "00000000-0000-4000-8000-000000000004" }
}
```

`expectedUid=null` is legal ONLY for `accountId=default`, meaning first discovery
of the already logged-in default game account. Named accounts require an expected
UID. The official `Bv.Uid()` OCR determines observed UID. No account switching or
login credentials are added. Identity failure is an identity diagnostic, never an
automatic human/login verdict. Identity mismatch is never accepted.

Every events.jsonl envelope contains schemaVersion=2, observerVersion,
type/eventType, observedAtUtc, processId, runId, runAttemptId, gameId, accountId,
gameDay, accountIdentityConfirmed, observedUid, taskId, taskName, operation,
todoAttemptId, outcome, data. `@identity` has no operation. The official final
`@check-rewards` commission check is associated with claim-daily-rewards when
selected. Its task_terminal alone never completes that composite operation.

- `account_identity`: data.observedUid, expectedUid, identityConfirmed,
  discovery, source, recognitionIds, reason.
- `task_started`: data.officialOrder, outcome=unknown.
- `official_task_fact`: data.phase, status, officialSource, details. Facts may be
  action_attempted, confirmed, insufficient_resource, unknown, failed, or other
  explicitly described official branch facts. These are not implicit terminals.
- `task_terminal`: data.outcome=completed/failed/skipped/cancelled/unknown,
  terminalConfirmed, reason, officialSource, facts dictionary, recognitionIds,
  successConfirmed. emitted at scope end. A return without a terminal branch
  remains unknown. Exceptions caught for retries remain diagnostics; terminal
  failures are emitted only at exhausted/propagated exception boundaries.
- `operation_terminal`: late completed composite report. It retains the original
  taskId, operation and todoAttemptId, with data.outcome, terminalConfirmed,
  reason, officialSource, facts, requiredPhases, recognitionIds and successConfirmed.
  claim-daily-rewards requires all six facts: encounter-points, catherine,
  expeditions, battle-pass-points, battle-pass-rewards, daily-commission-reward.
  UID must already be confirmed; original failed/cancelled cannot be overridden.
  A consumer may replace an earlier unknown with this explicit operation report.
  Manager screenshot verification is still required independently.
- `recognition`: data.recognitionId, raw.kind/path/sha256, method, phase,
  coordinateSpace, width/height, observedAtUtc, capturedAtUtc,
  capturedAtMeaning=official_recognition_input_observed, frameCapturedAt=null,
  frameCaptureTimeKnown=false, sealedAtUtc, source=official_input_copy, result.
  OCR result.text/boxes with actual scores; template result.boxes/threshold/mode.
  Coordinates always refer to archived pixels. Converted/cropped primitive
  inputs explicitly say original full frame unavailable.
- `recognition_sampling_missing`: bounded dedup/sample limit, not game failure.
- `task_exception`/`task_cancelled`: diagnostic unless terminalConfirmed true
  in a separate task_terminal; no inferred failure or success.
- `official_reward_result`: only final daily commission status, not all six
  composite components; Manager verification is independent.
- `upstream-log.jsonl`: full Serilog events, exception, template, properties.

At most two ordinary recognition images and six dedicated phase images per
operation. Metadata stays in JSONL; runner imports a scoped JSONL metadata
artifact plus image artifacts and complete logger, avoiding 20-artifact overflow.
Version 2 never captures at mouse/input boundaries and never delays 100 ms.
Version 1 historical action artifacts keep their original kind/timestamps.
Cancellation request file is checked only at existing official cancellable entry
points; `CancellationContext.Cancel()` remains the cancellation mechanism.
