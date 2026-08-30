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

The two reference scenes are `RoyalGlassLab/RoyalGlassLab.unity` and
`AllGlassesPlayground.unity`. Their rebuild recipes live in
`RoyalGlassLabBuilder.cs` and `AllGlassesPlaygroundBuilder.cs`.

## Adding or changing a vessel

1. Create or update a `VesselProfile` asset.
2. Assign the visible `front` sprite and, when necessary, a separate `traceSource`.
3. Bake the profile with `Tools > LiquidSort > Bake Selected Vessel Profiles`.
4. Validate the profile in the playground before using it in gameplay.

Do not replace a final profile with an old generated `_v2`, staged or comparison image.
The baked profile owns the geometry used by fill rendering and animation.
