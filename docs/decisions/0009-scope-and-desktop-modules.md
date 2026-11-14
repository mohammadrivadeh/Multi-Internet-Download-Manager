# ADR 0009: Scope reduction and modular WPF shell

- Date: 2026-11-14
- Status: Accepted; desktop runtime acceptance pending

The user excluded browser extensions and Site Grabber and requested an engineered WPF structure. Amend the active roadmap accordingly; keep other advanced protocol/media work explicit rather than silently dropping it.

Retain three production projects. Separate desktop shell/section views, presentation models, focused command partials, desktop adapters, resources and shared styles. App becomes the explicit composition root. Inject desktop and queue-session interfaces so SQLite initialization, native dialogs, resource lookup and scheduling construction are not hardwired in the view model. Avoid adding a generic container solely for composition.

Use a Grid-based two-panel shell: independently scrollable editor, bounded virtualized queue, and shared transfer status. Keep all current engine/queue behavior and awaited initialization/save/download shutdown. Preserve standard control templates/focus behavior and system-color resources. Timer ticks cannot overlap; callback failures become safe presentation errors instead of unobserved dispatcher exceptions.

The scope amendment does not certify the rest of the roadmap complete. Command partials are an intermediate organization; independent feature ViewModels and richer settings/details flows remain future vertical slices. Existing functional regressions validate engine and storage behavior, and WPF cross-compilation checks XAML/types. Real layout, binding/lifecycle and accessibility acceptance still require Windows. No mock or screenshot claim is substituted for that evidence.
