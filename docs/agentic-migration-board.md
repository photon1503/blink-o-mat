# Agentic Migration Board

This board operationalizes the spec in docs/agentic-coding-spec.md.

Use status values:
- TODO
- IN_PROGRESS
- BLOCKED
- DONE

Only mark DONE when the Acceptance Matrix is all Yes.

## Epic 1: Preview Parity
Goal: Match WPF preview interaction and visualization behavior.

### Task 1.0: FITS Viewer Pixel Fidelity, Zoom, and Pan Parity
- Status: IN_PROGRESS
- Priority: HIGH
- Objective: Verify that FITS pixels render without smoothing artifacts and complete WPF-equivalent zoom/pan controls.
- Baseline: src/PreviewWindow.xaml and src/PreviewWindow.xaml.cs (PreviewImage, FitToView, ZoomAroundViewerPoint, PreviewImage_MouseLeftButtonDown/Move/Up)
- Target: src/Rejector.Avalonia/Views/FramePreviewWindow.cs
- Validation data: `/Volumes/astronomy/RC/Bubble Nebula/LIGHT/R`
- Files expected: 1-3
- Report: docs/reports/1.0-fits-viewer-pixel-zoom-pan-parity.md
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: Yes
  - Remaining delta listed: Yes

### Task 1.1: ROI Handle Editing Parity
- Status: DONE
- Objective: Port WPF ROI move/resize handle semantics and constraints to Avalonia preview.
- Baseline: src/PreviewWindow.xaml.cs (RoiBody_MouseLeftButtonDown, RoiHandle_MouseLeftButtonDown, RoiOverlay_MouseMove)
- Target: src/Rejector.Avalonia/Views/FramePreviewWindow.cs
- Files expected: 1-3
- Report: docs/reports/1.1-roi-handle-editing-parity.md
- Acceptance Matrix:
  - Behavior matched: Yes
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: Yes
  - Remaining delta listed: Yes

### Task 1.2: Loupe / Pixel Inspector Parity
- Status: DONE
- Objective: Implement right-click loupe with pixel readout equivalent to WPF.
- Baseline: src/PreviewWindow.xaml.cs (ShowLoupeAt, BuildLoupeBitmap, PreviewImage_MouseRightButtonDown/Up)
- Target: src/Rejector.Avalonia/Views/FramePreviewWindow.cs
- Files expected: 1-4
- Report: docs/reports/1.2-loupe-pixel-inspector-parity.md
- Acceptance Matrix:
  - Behavior matched: Yes
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: Yes
  - Remaining delta listed: Yes

### Task 1.3: Orientation Debug Overlay Detail Parity
- Status: DONE
- Objective: Port orientation debug overlay details (labels, status text, match context).
- Baseline: src/PreviewWindow.xaml.cs (UpdateOrientationDebugOverlay)
- Target: src/Rejector.Avalonia/Views/FramePreviewWindow.cs
- Files expected: 1-3
- Report: docs/reports/1.3-orientation-debug-overlay-parity.md
- Acceptance Matrix:
  - Behavior matched: Yes
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: Yes
  - Remaining delta listed: Yes

### Task 1.4: Curvature Overlay Detail Parity
- Status: DONE
- Objective: Match curvature overlay behavior and visual feedback.
- Baseline: src/PreviewWindow.xaml.cs (curvature overlay and tooltip paths)
- Target: src/Rejector.Avalonia/Views/FramePreviewWindow.cs
- Files expected: 1-3
- Report: docs/reports/1.4-curvature-overlay-detail-parity.md
- Acceptance Matrix:
  - Behavior matched: Yes
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: Yes
  - Remaining delta listed: No

### Task 1.5: Cache Visualization and Jump Parity
- Status: DONE
- Objective: Match score strip, cache dots, active marker, and click navigation behavior.
- Baseline: src/PreviewWindow.xaml.cs (RedrawCacheIndicators)
- Target: src/Rejector.Avalonia/Views/FramePreviewWindow.cs
- Files expected: 1-3
- Report: docs/reports/1.5-cache-visualization-jump-parity.md
- Acceptance Matrix:
  - Behavior matched: Yes
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: Yes
  - Remaining delta listed: No

### Task 1.6: FITS Viewer Aspect Ratio Parity
- Status: DONE
- Priority: HIGH
- Objective: Preserve the source FITS aspect ratio in scaled viewer previews so images are not squeezed.
- Baseline: src/ViewModels/MainViewModel.cs (GetInteractivePreviewDimensions), src/PreviewWindow.xaml (PreviewImage)
- Targets: src/Rejector.Core/Services/RustafitsService.cs, tests/Rejector.Core.Tests/RustafitsServiceTests.cs
- Validation data: `/Volumes/astronomy/RC/Bubble Nebula/LIGHT/R`
- Files expected: 2-4
- Report: docs/reports/1.6-viewer-aspect-ratio-parity.md
- Acceptance Matrix:
  - Behavior matched: Yes
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: Yes
  - Remaining delta listed: No

## Epic 2: Settings and Profile Parity
Goal: Match WPF settings overlay behavior and persistence profile model.

### Task 2.0: Open Folder and Settings Surface Separation Parity
- Status: DONE
- Priority: HIGH
- Objective: Restore separate WPF-equivalent Open Folder popup and Settings overlay trigger paths and content ownership.
- Baseline: src/MainWindow.xaml (SessionSettingsButtonTopBar/SessionContextMenu), src/MainWindow.xaml.cs (SessionSettingsButtonTopBar_Click), src/Views/SettingsOverlayView.xaml
- Targets: src/Rejector.Avalonia/Views/MainWindow.axaml, src/Rejector.Avalonia/Views/MainWindow.axaml.cs, src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 3-5
- Report: docs/reports/2.0-folder-settings-separation-parity.md
- Acceptance Matrix:
  - Behavior matched: Yes
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: Yes
  - Remaining delta listed: Yes

### Task 2.1: Profile Lifecycle Parity
- Status: TODO
- Objective: Port profile create/select/default workflow.
- Baseline: src/Views/SettingsOverlayView.xaml and src/ViewModels/MainViewModel.cs
- Targets: src/Rejector.Avalonia/Views/MainWindow.axaml, src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 3-8
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: No
  - Remaining delta listed: No

### Task 2.2: Profile-Backed Toggle Persistence Parity
- Status: TODO
- Objective: Persist and restore all visibility/threshold/score toggles via SettingsProfile.
- Baseline: src/Models/SettingsProfile.cs, src/Services/AppSettingsService.cs, src/ViewModels/MainViewModel.cs
- Targets: src/Rejector.Core/Models/SettingsProfile.cs, src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 3-8
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: No
  - Remaining delta listed: No

### Task 2.3: Filter-Specific Threshold Workflows
- Status: TODO
- Objective: Match per-filter threshold editing and application behavior.
- Baseline: src/ViewModels/MainViewModel.cs (filter threshold logic)
- Target: src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 2-6
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: No
  - Remaining delta listed: No

## Epic 3: Sort, Filter, and Status Parity
Goal: Match rule precedence and status/performance semantics.

### Task 3.1: Multi-Rule Sort Precedence Validation
- Status: IN_PROGRESS
- Objective: Validate and fix any rule-order differences versus WPF comparator behavior.
- Baseline: src/ViewModels/MainViewModel.cs (ApplySorting, FrameItemComparer)
- Target: src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 1-4
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: Yes
  - Tests passed: Yes
  - Screenshot captured: No
  - Remaining delta listed: No

### Task 3.2: Filter Scope Statistics Consistency
- Status: TODO
- Objective: Align accepted/rejected stats and per-filter summary behavior with WPF.
- Baseline: src/ViewModels/MainViewModel.cs (GetVisibleFramesForStatistics and related)
- Target: src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 1-4
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: No
  - Remaining delta listed: No

### Task 3.3: Bottom Status/Performance Detail Parity
- Status: TODO
- Objective: Match WPF status lifecycle timing and detail granularity.
- Baseline: src/ViewModels/MainViewModel.cs
- Target: src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 1-3
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: No
  - Remaining delta listed: No

## Epic 4: Background Runtime Parity
Goal: Match runtime lifecycle helpers and background workflows.

### Task 4.1: Watch Folder Lifecycle Parity
- Status: TODO
- Objective: Port watcher start/stop/dedupe/add-file behavior.
- Baseline: src/ViewModels/MainViewModel.cs (StartFolderWatch and related)
- Target: src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 2-8
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: No
  - Remaining delta listed: No

### Task 4.2: Update Check Pipeline Parity
- Status: TODO
- Objective: Port update check service integration and update banner behavior.
- Baseline: src/Services/UpdateCheckService.cs and src/ViewModels/MainViewModel.cs
- Target: src/Rejector.Avalonia/ViewModels/MainWindowViewModel.cs
- Files expected: 2-5
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: No
  - Remaining delta listed: No

### Task 4.3: Window Placement Persistence for Avalonia
- Status: TODO
- Objective: Add main/preview window placement save and restore.
- Baseline: src/Services/WindowPlacementService.cs
- Targets: src/Rejector.Avalonia/Views/MainWindow.axaml.cs, src/Rejector.Avalonia/Views/FramePreviewWindow.cs
- Files expected: 2-5
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: No
  - Remaining delta listed: No

## Epic 5: Release and Packaging Cutover
Goal: Make Avalonia artifacts primary release outputs.

### Task 5.1: CI Artifact Ownership Cutover
- Status: TODO
- Objective: Produce and publish Avalonia artifacts for Windows and macOS.
- Baseline: .github/workflows/release.yml
- Targets: .github/workflows/release.yml and related scripts
- Files expected: 1-6
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: N/A
  - Remaining delta listed: No

### Task 5.2: Migration Exit Checklist Run
- Status: TODO
- Objective: Execute full parity checklist on Windows and macOS and record signoff.
- Baseline: docs/agentic-coding-spec.md
- Targets: docs/parity-signoff.md
- Files expected: 1-2
- Acceptance Matrix:
  - Behavior matched: No
  - Build passed: No
  - Tests passed: No
  - Screenshot captured: Yes
  - Remaining delta listed: No
