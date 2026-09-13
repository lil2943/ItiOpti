# Changelog

## 0.1.1

- Removed `.meta` files left behind by empty placeholder folders (`Editor/NDMF`, `Editor/Profiles`, `Runtime/Components`). Git does not keep empty folders, so these `.meta` files had no matching folder in the distributed package and caused Unity warnings.

## 0.1.0

- Added avatar analysis and import-setting recommendations.
- Added reversible texture import changes with redundant manifests.
- Added avatar-specific history, restore, and history deletion UI.
- Added non-destructive build-time texture atlasing.
- Preserved UV0 through UV7 and modern bone weights during mesh reconstruction.
- Renamed the user-facing product to ItiOptimiser while retaining internal IDs for compatibility.
