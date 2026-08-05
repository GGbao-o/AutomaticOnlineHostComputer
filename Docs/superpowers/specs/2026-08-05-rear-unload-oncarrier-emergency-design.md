# Rear Unload On-Carrier Emergency Recovery Design

## Goal

Remove the recovery deadlock caused when a successful skew-bed emergency clears
`bed.Wp`, but leaves the matching rear-crane unload `PauseOnCarrier` manual
action snapshot in memory.

## Scope

Apply the same narrow rule to Line 1 and Line 2 rear-crane engines:

- Preserve the existing rear-load emergency settlement behavior.
- Add a separate rear-unload settlement that removes only snapshots matching all
  of the following:
  - the engine's rear-unload `FlowScope`;
  - `Source == selectedBed.Code`;
  - `Disposition == PauseOnCarrier`.
- Run this settlement only after the existing emergency has successfully cleared
  device state, rear-crane task display state, and the selected skew-bed's
  software state.

## Explicitly excluded states

- `PauseAtTargetPendingHandoff` is not cleared. It represents a workpiece that
  may already be at an unload target such as M817/M720; removing it from a
  skew-bed emergency would make memory disagree with the physical target.
- `PauseSourceReserved` is not cleared. The source-side reservation requires
  its dedicated recovery path.
- Snapshots belonging to another line or a different skew bed are not touched.

## Error handling and operator result

If the device emergency fails or times out, no new unload snapshot is removed,
consistent with the existing early-return behavior. On success, the emergency
result log reports the number of matching rear-unload on-carrier snapshots that
were settled. A zero count is a valid no-op.

## Verification

Add mirrored contract coverage for both engines that requires the unload scope,
source-bed match, and `PauseOnCarrier` filter, while prohibiting a
`PauseAtTargetPendingHandoff` unload settlement. Then run the focused contract
test and the application build.
