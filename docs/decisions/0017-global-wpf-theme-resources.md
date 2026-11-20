# ADR 0017: Global WPF theme resources

- Date: 2026-11-20
- Status: Accepted

## Context

The WPF shell needed a clearer, modern visual hierarchy while preserving the download form, connection selection, queue actions, and transfer status. The user selected the Moonlight Glow direction and supplied seven WPF dictionaries from a related application: a shared style dictionary and six palettes. The approved product plan calls for a straightforward interface with advanced controls available on demand.

## Decision

Use a shared style dictionary and interchangeable palette resource dictionaries. Shared control styles consume palette values through dynamic resource references. Palette keys use the App.* namespace for background, surfaces, borders, text, accents, semantic colors, and typography.

Ship the supplied Moonlight Glow, Nord, Forest, Graphite, High Contrast, and Ivory palettes under application/App.Wpf/Themes. Moonlight Glow is the initial palette. Keep strings separate from visual resources. The shell, form, queue, and transfer status use shared palette resources rather than local color literals wherever practical.

The current composition selects one palette in App.xaml. Runtime palette selection is a future feature and should replace the selected palette dictionary while preserving shared styles. Persisting theme preference is also future work.

The shell uses a custom borderless WPF title bar with Segoe Fluent glyphs, drag-to-move, double-click maximize/restore, and minimize/maximize/close buttons. Sidebar destinations are clickable and expose Downloads, Queue, Connections, and Preferences. Connection selection uses discovered paths. Preferences surface saved defaults.

The Downloads page centers the queue across the available width. New download is a collapsed expandable editor, keeping URL, destination, and connection choices accessible without occupying the primary queue view. Advanced job settings remain grouped under Advanced options. Transfer status is a compact strip with one status line, progress, speed, and remaining time. The Queue page uses the full content area for filters, history, row actions, and selected details. The Connections page lays adapters out in wrapping cards with selection and address. Preferences uses paired setting fields and a distinct save action. Queue row, cell, and selection colors follow the global palette.

Connection selection and per-connection caps are managed on the Connections page and captured for newly created jobs. The New download editor contains the URL and destination, with the user's Downloads folder as the initial destination directory. If the destination still names that directory, the final URL path segment supplies a suggested filename. The primary OK action enqueues the job and starts queue processing. Preferences remains the place to save defaults.

The custom title bar is implemented with WPF mouse handling, not native non-client hit testing. Maximized-window resize behavior and keyboard/system-menu parity require Windows acceptance. Navigation currently switches page visibility using shell state. Runtime theme selection is not part of this increment.

## Consequences

- New reusable controls should use dynamic App.* resources for visual properties so palette changes propagate consistently.
- New palettes should define the same color keys as the supplied palette dictionaries.
- Theme changes remain centralized and do not require editing each view.
- Existing functional bindings and commands remain the source of behavior.
- Visual layout, keyboard navigation, accessibility, high contrast, and DPI require Windows desktop acceptance. A successful build alone does not validate rendered appearance.
- The user supplied screenshots showing a white queue row and crowded download form. Queue rows now use theme-aware backgrounds and foregrounds, while advanced settings are collapsed by default.
- The four supplied page screenshots showed excess unused space, a narrow download editor, verbose status text, and adapter addresses that were difficult to scan. The page layouts were revised around a queue-first Downloads view, compact status, full-width queue/preferences surfaces, and wrapping connection cards. Release build succeeded with zero warnings/errors after this revision. Live rendered acceptance remains pending.
- The user supplied an Internet Download Manager screenshot as a layout reference. Keep the Moonlight Glow design and shared resources, while adopting the familiar information hierarchy: categories and queues in the left rail, labeled icon actions along the page header, and a central download list with size, state, progress, and job details. Download rows show an in-row progress bar and human-readable decimal KB/MB/GB amounts. Connection configuration stays in Connections. This is a layout reference, not a feature parity claim. The Release WPF build succeeds; visual and interaction acceptance still requires running on Windows.
- Custom queues are now represented by protected per-queue preferences and a queue name on each protected download record. Older records without that field use Main. Users can create, rename, delete, and select queues; deleting a queue moves its jobs to Main, and a selected download can be moved to another queue. Each queue has an independent runner, a parallel-download limit from 1 to 32 or All (capped at 32), and optional daily local start/stop times including overnight windows. When a scheduled stop interrupts downloads, those jobs are paused and queued for the next start. The scheduler requires the app to remain open and the queue to be started. Release build and queue-focused tests pass. The full integration suite has one environment-dependent DPAPI profile test failure on this machine; Windows UI acceptance remains pending.
- A Windows runtime error showed the progress bar attempting to write back to its read-only row percentage. Its binding is now explicitly one-way. The Release build succeeds after the fix; live UI acceptance remains pending.
