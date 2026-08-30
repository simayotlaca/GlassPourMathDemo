# LiquidSort runtime

The runtime renders and animates layered liquid inside pre-baked vessel profiles.

## Main components

| Component | Responsibility |
| --- | --- |
| `VesselProfile` | Baked interior geometry, capacity, fill tables and pour pose |
| `LiquidBottle` | Liquid state and shader data |
| `BottleShell` | Authored glass front, contour, shadow and theme |
| `WaterSortBoard` | Legacy/standalone sandbox selection and transfer rules |
| `PourAnimator` | Carry, tilt, transfer, settle and return sequence |
| `PourStream` | Procedural stream mesh |
| `BartenderLevelController` | Authoritative campaign board and commands |
| `BartenderShelfLevelView` | Royal pool, shelf layout and board presentation |
| `BartenderPourInteraction` | Camera input and domain-first pour animation bridge |

The canonical glass reference scene is `RoyalGlassLab/RoyalGlassLab.unity`; its
rebuild recipe lives in `RoyalGlassLabBuilder.cs`. Royal profiles, art and liquid
material are the authoritative snapshots used by that builder. Missing canonical
assets stop the rebuild instead of being silently replaced from an older profile.

The retired `AllGlassesPlayground` scene is kept only in an external local archive.
Restore that archive into a disposable project if it is ever needed. The generic
profiles and source sprites that remain here are source-library assets for future
vessel work, not a second reference scene.

## Portable Bartender shelf rig

Run `Tools > LiquidSort > Rebuild Sorting Shelf Showcase` to produce both:

- `SortingShelfShowcase.unity`, the scene-native authoring/preview scene on isolated layer 28.
- `Prefabs/BartenderShelfRig.prefab`, the scene-transfer unit recursively saved on Default
  layer with identity root transform.

Drag the prefab into a target scene. The target owns its camera and audio listener; the
prefab deliberately contains neither. Leave `BartenderPourInteraction.inputCamera` empty
to resolve a tagged `Camera.main`, or inject a scene camera explicitly. The camera culling
mask must include Default. Do not add `WaterSortBoard` beside the Bartender components:
both systems own rules, while only the Bartender chain matches this shelf/campaign view.

The prefab carries pointer selection, legal pour commits, synchronization deferral,
presentation locking, `PourAnimator`, and its authored `PourStream`. Order UI is a separate
consumer and must call `BartenderLevelController.TryDeliver`; the rig does not guess a
delivery gesture. With `resumeSavedProgress` enabled, the controller uses PlayerPrefs key
`LiquidSort.Bartender.NextLevelSlot`. A completed save therefore opens in
`CampaignComplete`; disable resume and choose `startingLevelNumber` for a deterministic
integration scene.

## Adding or changing a vessel

1. Create or update a `VesselProfile` asset.
2. Assign the visible `front` sprite and, when necessary, a separate `traceSource`.
3. Bake the profile with `Tools > LiquidSort > Bake Selected Vessel Profiles`.
4. Validate the profile in an isolated test scene before using it in gameplay.

Do not replace a final profile with an old generated `_v2`, staged or comparison image.
The baked profile owns the geometry used by fill rendering and animation.
