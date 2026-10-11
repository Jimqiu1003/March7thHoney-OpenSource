# Gacha rebuild: new catalog, rule engine, planner and executor

Baseline: `fe00bdbda3c28ca33d01f171566aa35b5ab95083`, branch `4.6`.
Display investigation target: `CNPRODWin4.6.0 / 16566615_V4.6Live`.

## Changes

- `BannerConfig` retains its serialized members and public entrypoints. `GachaRules` is newly implemented as staged state transitions, with an injectable `Random` and an explicit state copy. Fixed-seed comparisons preserve existing chances, guarantees, category choices and random-call order.
- `GachaCatalog` creates a request-local resource index and one validated active catalog for page and draw paths. `GachaManager` is newly implemented as validation, complete draw/reward planning, capacity checks, charging and reward application. Duplicate conversion is planned against temporary ownership/copy counters before charging, including duplicates crossing the max-rank boundary within one batch. Material overflow is rejected before charging.
- `GachaRewards` centralizes duplicate conversion. A confirmed null-count bug is corrected in both the response and avatar-card inventory helper: a rank-six avatar with no held rank-up material must receive the maximum duplicate conversion, without an extra rank-up material.
- `GachaProtocol` centralizes banner and draw response construction. No unknown field is assigned a guessed meaning; no protocol definitions or generated messages have been changed.

- Page initialization now includes the selection structure already used by the existing selection-set response, once per active avatar event/collaboration banner. Both use the same projection. This is a compatibility candidate, not proof that the target client parses that structure correctly.
- Page and draw paths share candidate validation. Duplicate banner IDs and missing reachable reward definitions are rejected before charging. The old invalid-result-to-three-star replacement is removed. Existing `RateUpItems4` files contain five-star preview entries: the serialized configuration is retained, the draw rules keep their rarity filtering, and the page's four-star list now contains only actual four-star candidates.
- Ten pulls clone pity state once, then advance the staged state in sequence. Selection mutations and page reads share the draw gate. The network handler uses the asynchronous selection entrypoint; the original synchronous entrypoint remains available for existing callers.
- An exception after settlement begins blocks further draws/selection changes for that cached player state, including managers recreated on reconnect. It logs only the exception type. Inventory-sync failure after successful rewards does not roll back or re-execute the draw; reconnect refreshes inventory.
- The page projector is newly implemented by banner kind rather than special-case ID 1001. All banners expose their configured time window. Collaboration banners no longer inherit the working regular avatar-event marker. These known-field changes require native-client acceptance; unknown fields remain untouched.

Rejected expired/unknown pools, insufficient inventory capacity and invalid resource IDs are intentional validation changes. Save schema, history, existing pity-family keys, probabilities and newbie cost remain unchanged. Failed database batches retain the existing rollback and retry behavior. Settlement quarantine is process-local; restarting does not repair a partial settlement. An operator must reconcile affected inventory/history/pity before allowing further draws. The existing periodic-save window remains; this is not a durable transaction system.

## Checks

```powershell
dotnet run --project tests/GachaRegression/GachaRegression.csproj -c Release
dotnet build March7thHoney.sln -c Release
```

The dependency-free assertion runner compares 24,000 seeded draws against a frozen rule oracle from the baseline, including full pity state and legacy fields. It checks banner wire bytes, reward conversion, invalid request immutability, one/ten draws, newbie discount, concurrent ticket spending, pool selection, the actual max-rank inventory helper, SQLite reload, and rollback/retry under a synthetic save failure. Its expected save-failure log is intentional. All databases and players in the runner are synthetic; it does not open an operator's runtime data or configuration.

The frozen test oracle must not be modernized with production code. No new package is required.

An optional single argument points to a read-only `Resource.bin` fixture; the runner uses the repository's banner configuration copied into its output as a fixture, never an operator's runtime banner file. All players and databases remain synthetic. This checks the actual catalog and one/ten draws for every active banner, including response-to-inventory material, avatar and equipment deltas. Do not commit local resource caches. Page assertions cover selection consistency and four-star filtering rather than requiring the whole initialization response to remain byte-identical. Batch item/state parity and quarantine after an injected settlement failure are covered.

## Display issue and release gate

[Issue #4](https://github.com/Mar7thLover/March7thHoney-OpenSource/issues/4) remains unresolved until native-client acceptance. Captured server timings and successful draws do not prove correct client parsing or display.

The target client's current log identifies `CollaborationGachaMergeGroupTabItemPanel._init_single_tab_control_pool`: its avatar-pool count assertion receives **0 pools for 2 fixed tab buttons**. Its static Lua implementation initializes both subgroups from `GetSortedGachaPoolGroups()`. Read-only inspection of the same client's static design data confirms group 31 members: `5003, 5004, 6003, 6004, 5001, 5002, 6001, 6002`. The old server configuration supplies only the last four. Static prefab records associate the first four with `1508, 1509, 23061, 23062`, respectively. No client assets, protocol definitions or unknown fields are changed.

The default configuration now supplies all eight members using the existing collaboration probability and pity families. The shared catalog excludes the entire fixed merge group when a member is absent, duplicated, expired, of the wrong type, or has invalid reward resources. This prevents incomplete page initialization and makes draw/selection availability match the page. A custom installation must update its existing banner file explicitly; publishing assemblies alone does not migrate it. Regression checks cover the old four-member configuration and each incomplete-member case while retaining the standard pool and preserving tickets/pity on rejection.

This is a confirmed collaboration initialization failure with a targeted candidate correction. Later standard-pool and cutscene failures may follow that aborted initialization, but their recovery is not yet verified. Native acceptance must still check those symptoms independently.

A ready PR claiming the display fix requires live-client acceptance of standard, event and collaboration banners, correct ticket/discount display, single and ten-draw results, insufficient-ticket rejection, inventory correspondence and persistence after reconnect. A protocol patch additionally requires verified target-build mapping or a trustworthy response sample; a resource patch requires a located same-version file. Until those gates pass, retain the local branch without claiming the UI is repaired.

## Reference provenance

Architecture ideas were reviewed from:

- [LunarCore](https://github.com/Melledy/LunarCore/tree/04b5de871c62895091c4d65243b2e03997e66f23), `GachaBanner` and `GachaService`: separate banner projection and execution.
- [DanhengServer-Public](https://github.com/Luotianyi-0712/DanhengServer-Public/tree/ef18bc2294d8a15d61bc4a0c7b29b4de6da2871c), `GachaManager`: keep inventory integration and reward synchronization together.

These references are not evidence for this CN live build's wire mapping. Their probability implementations and generated protocol fields were not copied. The new implementation uses the repository's existing license and preserves its economic contract through the frozen baseline, rather than retaining the old control flow.

## Local deployment

Publish into a unique directory carrying the commit, keep an SHA256 manifest, and retain original assemblies. Before a runtime switch, exit the client normally, stop the server normally, and back up current data. Roll back assemblies and only the specifically changed resource files; do not restore an older database over subsequent progress. Client captures are temporary, outside this PR, and must be removed after use. VPN, proxy and tunneling configuration are outside this change.
