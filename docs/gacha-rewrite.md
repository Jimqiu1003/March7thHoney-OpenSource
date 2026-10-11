# Gacha rewrite: behavior preserved, display fix pending

Baseline: `fe00bdbda3c28ca33d01f171566aa35b5ab95083`, branch `4.6`.
Display investigation target: `CNPRODWin4.6.0 / 16566615_V4.6Live`.

## Changes

- `BannerConfig` retains its serialized members and public entrypoints. `GachaRules` computes on a copied pity state, with an injectable `Random` for deterministic comparisons. The existing chances, guarantees, category choices and random-call order are preserved.
- `GachaManager` validates requests, plans all results, checks inventory capacity and then charges tickets and applies rewards through existing managers. A gate shared by cached gacha data serializes draws across reconnect-created managers. Invalid requests do not create a pity entry.
- `GachaRewards` centralizes duplicate conversion. A confirmed null-count bug is corrected in both the response and avatar-card inventory helper: a rank-six avatar with no held rank-up material must receive the maximum duplicate conversion, without an extra rank-up material.
- `GachaProtocol` centralizes banner and draw response construction. No unknown field is assigned a guessed meaning; no protocol definitions or generated messages have been changed.

Rejected expired/unknown pools, insufficient inventory capacity and invalid featured-resource IDs are intentional validation changes. Save schema, history, existing pity-family keys, probabilities and newbie cost remain unchanged. Failed database batches retain the existing rollback and retry behavior. The existing periodic-save window and unexpected failures during inventory application remain limitations; this is not a new durable transaction system.

## Checks

```powershell
dotnet run --project tests/GachaRegression/GachaRegression.csproj -c Release
dotnet build March7thHoney.sln -c Release
```

The dependency-free assertion runner compares 24,000 seeded draws against a frozen rule oracle from the baseline, including full pity state and legacy fields. It checks banner wire bytes, reward conversion, invalid request immutability, one/ten draws, newbie discount, concurrent ticket spending, pool selection, the actual max-rank inventory helper, SQLite reload, and rollback/retry under a synthetic save failure. Its expected save-failure log is intentional. All databases and players in the runner are synthetic; it does not open an operator's runtime data or configuration.

The frozen test oracle must not be modernized with production code. No new package is required.

## Display issue and release gate

[Issue #4](https://github.com/Mar7thLover/March7thHoney-OpenSource/issues/4) remains unresolved. Captured server timings and successful draws do not prove correct client parsing or display. A previously captured Lua nil-index category had no usable function stack. The rewrite deliberately preserves the old banner wire bytes while reliable mapping/resource evidence is unavailable.

A ready PR claiming the display fix requires live-client acceptance of standard, event and collaboration banners, correct ticket/discount display, single and ten-draw results, insufficient-ticket rejection, inventory correspondence and persistence after reconnect. A protocol patch additionally requires verified target-build mapping or a trustworthy response sample; a resource patch requires a located same-version file. Until those gates pass, retain the local branch without claiming the UI is repaired.

## Reference provenance

Architecture ideas were reviewed from:

- [LunarCore](https://github.com/Melledy/LunarCore/tree/04b5de871c62895091c4d65243b2e03997e66f23), `GachaBanner` and `GachaService`: separate banner projection and execution.
- [DanhengServer-Public](https://github.com/Luotianyi-0712/DanhengServer-Public/tree/ef18bc2294d8a15d61bc4a0c7b29b4de6da2871c), `GachaManager`: keep inventory integration and reward synchronization together.

These references are not evidence for this CN live build's wire mapping. Their probability implementations and generated protocol fields were not copied. The implementation moves and restructures Honey's own baseline code under the repository's existing license.

## Local deployment

Publish into a unique directory carrying the commit, keep an SHA256 manifest, and retain original assemblies. Before a runtime switch, exit the client normally, stop the server normally, and back up current data. Roll back assemblies and only the specifically changed resource files; do not restore an older database over subsequent progress. Client captures are temporary, outside this PR, and must be removed after use. VPN, proxy and tunneling configuration are outside this change.
