# Motion Page Magnet Feedback Design

## Goal

Show the selected crane or manipulator's actual magnet feedback beside device
selection on the motion-parameter page, so an operator can distinguish
magnetized and demagnetized states before manual movement.

## User interface

Add a compact, persistent status card in the existing `设备选择` group:

- `充磁：到位 / 未到位 / 读取未知` from PLC input X6.
- `退磁：到位 / 未到位 / 读取未知` from PLC input X7.
- Green means the corresponding feedback is on; neutral means its feedback is
  off; amber/gray means a read was not obtained. Both feedbacks are displayed
  independently so contradictory X6=1 and X7=1 data remains visible rather
  than being guessed into a safe-looking state.
- Provide a `刷新磁铁状态` button. Device selection, `读取当前位置`, and a
  successful charge or release command also request a refresh.

## Data flow

`CraneManualControlViewModel` continues to obtain the selected device through
the existing crane/manipulator connection caches. It reads `CraneService`
X6 (`D_X6_MagnetizeOk`) and X7 (`D_X7_DemagnetizeOk`) feedback with the
existing connection; no second client or background polling loop is created.

Each feedback value is nullable. A read exception or timeout produces
`读取未知`, never `未到位`, so communication loss cannot be shown as a safe
demagnetized result. On device changes, clear the prior device's visible
feedback before requesting the new device's values.

## Scope and safety

- This is display-only: it does not block, enable, or automatically issue a
  movement, charge, or release command.
- No continuous polling is added, avoiding contention with manual motion and
  the shared PLC connection.
- The status reports magnet feedback only; it does not assert that a workpiece
  is attached or released.

## Verification

Add source-contract coverage for the two X-input reads, nullable unknown
handling, and XAML bindings. Build the application and run the full test suite.
