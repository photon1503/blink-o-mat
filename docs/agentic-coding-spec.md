# Agentic Coding Spec: WPF to Avalonia Parity Migration

## 1. Purpose
Define how autonomous coding agents execute, validate, and report work for migrating the legacy WPF Rejector app to the Avalonia cross-platform stack while preserving behavior and visual parity.

This spec is the source of truth for:
- What agents are allowed to change.
- How tasks are decomposed.
- How correctness is verified.
- What evidence must be produced before a task is marked done.

## 2. Scope
In scope:
- Feature and UI parity from legacy WPF in src/ to Avalonia in src/Rejector.Avalonia/.
- Shared logic parity in src/Rejector.Core/ and CLI consistency in src/Rejector.Cli/.
- Build/test/review artifacts needed for a safe migration.

Out of scope unless explicitly requested:
- New product features not present in WPF baseline.
- Major UX redesigns.
- Algorithmic behavior changes that alter rejection outcomes.

## 3. Migration Principles
1. Parity-first.
- Do not invent behavior while parity gaps exist.

2. Core correctness first, UI second.
- Shared logic in Rejector.Core is the behavior baseline.

3. Small vertical slices.
- Implement one user-visible workflow at a time with tests and screenshots.

4. Evidence over claims.
- Every completed task includes build/test output summary plus visual proof when UI changed.

5. No silent regressions.
- Any changed behavior must be explicit and approved.

## 4. Canonical Baseline
WPF baseline references:
- Main workflow and sort/filter: src/ViewModels/MainViewModel.cs
- Settings overlay: src/Views/SettingsOverlayView.xaml
- Preview behavior: src/PreviewWindow.xaml and src/PreviewWindow.xaml.cs
- Frame cards: src/Controls/FrameListItemView.xaml

Avalonia targets:
- Main shell: src/Rejector.Avalonia/Views/MainWindow.axaml
- Main orchestration: src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Preview window: src/Rejector.Avalonia/Views/FramePreviewWindow.cs

Available FITS validation data:
- `/Volumes/astronomy/RC/Bubble Nebula/LIGHT/R`
- Use these real frames for preview, pixel-fidelity, zoom, pan, overlay, and screenshot validation when the volume is mounted.

## 5. Agent Roles
### 5.1 Primary Implementation Agent
Owns coding tasks end-to-end:
- Analyze baseline behavior.
- Implement smallest viable parity patch.
- Build, test, and capture screenshots.
- Report residual gaps and risks.

### 5.2 Review Agent (optional pass)
Performs parity and regression review:
- Confirms behavior against WPF baseline.
- Flags missing interactions and edge cases.
- Verifies no accidental scope creep.

### 5.3 Search Agent (optional helper)
Finds source-of-truth code paths quickly:
- Symbol discovery.
- Legacy-to-Avalonia mapping.

## 6. Task Lifecycle
Each task follows this mandatory loop.

1. Define task contract.
- User behavior to match.
- Files likely to change.
- Acceptance criteria.

2. Baseline extraction.
- Read WPF implementation and identify exact behavior triggers.

3. Implementation.
- Apply focused changes in Avalonia/Core only as needed.

4. Validation.
- Build:
  - dotnet build src/Rejector.Avalonia/Rejector.Avalonia.csproj
  - dotnet build Rejector.CrossPlatform.slnx
- Tests:
  - dotnet test tests/Rejector.Core.Tests/Rejector.Core.Tests.csproj

5. Visual parity evidence (for UI tasks).
- Capture updated screenshot(s).
- Compare to legacy screenshot reference.
- Record observed deltas.

6. Completion report.
- What changed.
- What passed.
- Remaining gap list.

## 7. Done Criteria
A task is Done only if all checks pass.

### 7.1 Functional
- Feature works via same trigger path as WPF baseline.
- Behavior is deterministic and repeatable.

### 7.2 Technical
- Avalonia project builds cleanly.
- Cross-platform solution builds cleanly.
- Core tests pass.

### 7.3 Parity Evidence
- Before/after screenshots captured for changed UI behavior.
- Any remaining visual/interaction mismatch explicitly listed.

### 7.4 Documentation
- Task log includes commands run and key outcomes.

## 8. Non-Negotiable Guardrails
1. Do not remove or rewrite existing behavior unless parity requires it.
2. Do not introduce speculative features.
3. Do not commit hidden migration assumptions.
4. Do not close parity tasks without verification evidence.
5. Do not claim visual parity from code inspection alone.

## 9. Work Packaging
### 9.1 Epic Structure
Use these parity epics:
1. Preview parity.
2. Settings/profile parity.
3. Sort/filter/status parity.
4. Watch-folder/update/window-state parity.
5. Release and packaging cutover.

### 9.2 Task Unit Size
Preferred task size:
- 1 to 3 files changed for simple behavior parity.
- 3 to 8 files for cross-layer parity slices.

If more than 8 files are needed, split the task unless explicitly requested.

## 10. Acceptance Matrix (Required)
For each task, write this matrix in the task report.

- Behavior matched: Yes/No
- Build passed: Yes/No
- Tests passed: Yes/No
- Screenshot captured: Yes/No
- Remaining delta listed: Yes/No

Task cannot be marked complete if any item is No.

## 11. Standard Command Set
Use these commands as default verification.

- Build Avalonia:
  - dotnet build src/Rejector.Avalonia/Rejector.Avalonia.csproj

- Build full migration solution:
  - dotnet build Rejector.CrossPlatform.slnx

- Run core tests:
  - dotnet test tests/Rejector.Core.Tests/Rejector.Core.Tests.csproj

- Run app:
  - dotnet run --project src/Rejector.Avalonia/Rejector.Avalonia.csproj

- Capture screenshot:
  - screencapture -x <path>

## 12. UI Parity Quality Bar
For each migrated surface, verify:
- Trigger parity: same click/key path.
- State parity: same enabled/disabled and visibility rules.
- Data parity: same values and formatting.
- Interaction parity: same navigation and edit semantics.
- Feedback parity: same status/progress/error messaging intent.

## 13. Risk Register (Active)
Track and update these risks each sprint:
1. Preview mismatch risk.
- Deep overlays and ROI edit behavior can diverge subtly.

2. Settings drift risk.
- Profile-backed toggles and score weights may desync between Core model and Avalonia bindings.

3. Sort/filter drift risk.
- Multi-rule sorting and visibility filters can regress frame ordering.

4. Background workflow risk.
- Watch-folder and async preview cache paths can introduce race conditions.

## 14. Reporting Template
Use this exact structure in future agent updates.

1. Objective
- One sentence stating parity behavior targeted.

2. Baseline reference
- Legacy files/symbols used as source of truth.

3. Changes made
- Files changed and behavior implemented.

4. Validation
- Build and test outcomes.

5. Visual evidence
- Screenshot paths and comparison notes.

6. Remaining deltas
- Explicit list of still-open parity gaps.

## 15. Sprint Plan for Remaining Open Topics
Priority order:

1. Complete preview parity internals.
- HIGH: Verify FITS viewer pixel fidelity and complete zoom/pan interaction parity.
- ROI handle edit modes and constraints.
- Loupe and pixel inspector.
- Orientation and curvature overlay detail parity.
- Cache behavior parity under navigation and filtering.

2. Complete settings/profile parity.
- HIGH: Keep Open Folder/session controls and the Settings overlay as separate WPF-equivalent surfaces.
- Full profile lifecycle and default selection.
- Profile-backed persistence for all visibility/threshold/weight toggles.
- Filter-specific threshold workflows.

3. Complete sort/filter/status parity.
- Rule precedence and direction semantics.
- Per-filter summaries and status/performance consistency.

4. Complete background/runtime parity.
- Watch-folder lifecycle and dedupe.
- Update check pipeline and banner behavior.
- Window placement persistence for main and preview windows.

5. Release cutover.
- Move release ownership to Avalonia artifacts in CI.

## 16. Exit Criteria for Migration
Migration is complete when all are true:
1. All parity epics marked done with evidence.
2. Behavior checklist passes on macOS and Windows.
3. Release pipeline publishes Avalonia artifacts as primary.
4. WPF app is no longer required for standard user workflow.

## 17. Implementation Artifacts
The following files implement this spec as an operational workflow:

- Migration board: docs/agentic-migration-board.md
- Run protocol: docs/agentic-run-protocol.md
- Task report template: docs/agentic-task-report-template.md
- Reports folder: docs/reports/README.md
- Verification script: scripts/verify-parity.sh

## 18. Quick Start
1. Pick one task in docs/agentic-migration-board.md and set Status to IN_PROGRESS.
2. Create a report file in docs/reports/ from docs/agentic-task-report-template.md.
3. Implement parity slice.
4. Run scripts/verify-parity.sh.
5. Capture screenshots for UI changes.
6. Fill Acceptance Matrix and update task status.
