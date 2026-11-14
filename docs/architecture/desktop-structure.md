# Desktop structure and ownership

```text
application/
  Core/                     Domain policies, intervals, queue scheduling
  Infrastructure/
    Downloads/              Engine orchestration
    Http/                   HTTP validation, redirect and rate policies
    Networking/             Discovery and selected-path pools
    Persistence/            SQLite checkpoints and protected queue storage
    Queueing/               Queue runner and execution adapter
  App.Wpf/
    App.xaml(.cs)           Resource merge and composition root
    Views/Shell/            Window layout and awaited shutdown lifecycle
    Views/Sections/         New download, virtualized queue, transfer status
    ViewModels/             Shell state and focused command partials
    Models/                 Presentation rows and path selection
    Services/               Dialog/text/dispatcher/timer and queue-session adapters
    Resources/              User-visible string dictionaries
    Themes/                 Shared control styles and system color usage
```

App constructs the queue session, desktop adapters, view model and window. Views inherit shell DataContext and contain no engine or persistence logic. Code-behind initializes controls; the window also coordinates async startup/close. The view model owns presentation state and commands, not SQLite construction or Win32 dialogs. The queue session owns protected storage until all commands/writers finish. Timers suppress overlapping ticks and stop during shutdown. Progress reaches the dispatcher through the existing throttled adapter and stale-run guard.

Command partials are an incremental shell decomposition, not independent feature ViewModels. New behavior should receive an explicit owner and tests; split independent feature state when its lifecycle warrants it. Do not add service locators, global mutable containers or empty abstraction projects.

The main Grid has a bounded queue viewport; do not wrap the entire window or DataGrid in an unbounded outer ScrollViewer. Only the editor scrolls. Keep keyboard/default focus behavior, resource-backed labels and system colors. Validate scaling, keyboard order, screen readers and high contrast on real Windows before claiming accessibility acceptance.

Production code stays under application/; tests and test executables stay under tests/. No browser extension, native messaging or Site Grabber project is authorized.
