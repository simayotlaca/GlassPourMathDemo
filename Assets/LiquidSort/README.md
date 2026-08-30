# LiquidSort runtime

The runtime renders and animates layered liquid inside pre-baked vessel profiles.

## Main components

| Component | Responsibility |
| --- | --- |
| `VesselProfile` | Baked interior geometry, capacity, fill tables and pour pose |
| `LiquidBottle` | Liquid state and shader data |
| `BottleShell` | Authored glass front, contour, shadow and theme |
| `WaterSortBoard` | Selection and legal transfer rules |
| `PourAnimator` | Carry, tilt, transfer, settle and return sequence |
| `PourStream` | Procedural stream mesh |

The canonical glass reference scene is `RoyalGlassLab/RoyalGlassLab.unity`; its
rebuild recipe lives in `RoyalGlassLabBuilder.cs`. Royal profiles, art and liquid
material are the authoritative snapshots used by that builder. Missing canonical
assets stop the rebuild instead of being silently replaced from an older profile.

The retired `AllGlassesPlayground` scene is kept only in an external local archive.
Restore that archive into a disposable project if it is ever needed. The generic
profiles and source sprites that remain here are source-library assets for future
vessel work, not a second reference scene.

## Adding or changing a vessel

1. Create or update a `VesselProfile` asset.
2. Assign the visible `front` sprite and, when necessary, a separate `traceSource`.
3. Bake the profile with `Tools > LiquidSort > Bake Selected Vessel Profiles`.
4. Validate the profile in an isolated test scene before using it in gameplay.

Do not replace a final profile with an old generated `_v2`, staged or comparison image.
The baked profile owns the geometry used by fill rendering and animation.
