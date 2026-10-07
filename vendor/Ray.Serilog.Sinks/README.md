# Notification dependency sources

The batch core originates from [Ray.Serilog.Sinks](https://github.com/RayWangQvQ/Ray.Serilog.Sinks), licensed under GPL-3.0. The license is included in this directory and in the batch core project.

This fork carries reviewed queue and transport fixes as source projects until corresponding NuGet releases are available. The application references these projects directly, with no private package feed or committed package binaries.

- `src/Ray.Serilog.Sinks.Batched`: batch queue, flush and disposal behavior. Keeps the `0.1.5.0` assembly identity used by existing channel packages.
- `src/Ray.Serilog.Sinks.Compatibility`: HTTP, Telegram and Work Weixin transport adapters.
- `test/Notification.DependencyTests`: queue integrity regression tests.

Application notification policy and configuration mapping remain in the application projects.
