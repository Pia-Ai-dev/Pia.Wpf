# Follow-up prompt — ingest plugin (GUID …007) server/client sync coordination

Copy the block below into a new session started with BOTH repos available (client `Pia.Wpf` and server `../Pia`).

---

We added a **client-only** built-in "ingest" tool-pack plugin to the Pia WPF client in commit `e0e9f5b` on branch `feature/meeting_attendee`. Its well-known GUID is `10000000-0000-0000-0000-000000000007`. Unlike the other built-in plugins (`…001`–`…006`), this GUID is **not** in the server seed data — `BuiltInPluginDefaults` comments say the other GUIDs "match server seed data", but …007 does not yet.

**Concern to resolve:** when a user toggles the ingest plugin, `PluginService.SetPluginEnabledAsync` (src/Pia.Wpf/Services/Plugins/PluginService.cs) queues a `SyncPluginPreference` for GUID …007. That queue is peek-not-drained (`GetPendingPreferenceChanges`) and only cleared by `ClearPreferenceChangesAfterSuccessfulPush` after a **successful** push. If the server's plugin-preference sync path rejects a preference that references an unknown plugin id (FK violation, 400, or a whole-request failure), the push fails and the ENTIRE preference queue can wedge — blocking preference sync for all plugins, not just ingest.

**Do this:**
1. **Determine server behavior.** In `../Pia`, find the plugin-preference sync endpoint/handler and the EF model for plugin preferences. Does a preference for an unknown/unseeded plugin GUID get safely ignored, or does it error (FK constraint / validation / 500) and fail the whole push? Also check how the client push handles a partial/failed response (client side: `SyncClientService` push path + `PluginService.GetPendingPreferenceChanges` / `ClearPreferenceChangesAfterSuccessfulPush`).
2. **Decide the fix** based on (1):
   - **Server seed:** add the ingest plugin (GUID …007, kind `builtin_tool_pack`, name `ingest`) to the server's built-in plugin seed so the GUID is known (mirror how …001–…006 are seeded), OR
   - **Client resilience:** don't enqueue sync preferences for preloaded built-in plugins (or make the push tolerate per-plugin rejection so one unknown id can't wedge the queue), OR
   - **Both.**
3. **Implement + test** on whichever side(s) the decision requires. On the client, gate the test suite per the memory baseline (MTP runner, exclude `Pia.Wpf.Tests.Integration.Providers`, 3 known pre-existing non-Provider failures). On the server, build the `.csproj` directly (the `Pia.slnx` build is a no-op) and use its own test baseline.
4. Keep the client and server in sync on the GUID/name if you choose to seed server-side.

Context files (client): `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs` (the …007 entry + `PreloadedPluginIds`), `src/Pia.Wpf/Services/Plugins/PluginService.cs` (`SetPluginEnabledAsync`, `ApplyServerPluginsAsync`, the pending-preference queue), and the sync push in `SyncClientService`.

---
