# LiquidSort

Water/liquid sort liquid rendering and pouring for Unity 6. **No editor tools, no extra
packages, no build step.** Put the component on a GameObject, give it your glass drawing,
press Play.

## Setup

1. Add `LiquidBottle` to a GameObject.
2. Drop your glass artwork into its **Glass Art** field.
3. Tick **Read/Write** on that texture's import settings (the interior is traced from its
   pixels, so they have to be readable).

That is the whole configuration. On Awake the bottle traces the drawing and works out its
own interior polygon, pour lip, whether the rim is open or a narrow neck, and how far down
its own outline hides the liquid. A different glass is a different drawing and nothing
else — no numbers to copy, no tool to run, nothing to re-bake when the art changes.

Add `BottleShell` alongside it to get the dark interior behind the liquid, and
`WaterSortBoard` + `PourAnimator` on a parent for the puzzle and the pouring.

## What is going on

| Piece | File | Idea |
| --- | --- | --- |
| Interior from art | `GlassInteriorFitter.cs` | Flood fill from the border marks the outside; the largest transparent pocket the outline encloses is the bowl. Traced, simplified, converted to local units. |
| Waterline math | `VesselFillMath.cs` | Rotate the interior into the "liquid frame", bisect for the height that leaves the right area below it. Volume holds at any tilt. |
| Rendering | `BottleLiquid.shader` | Up to 8 bands, one draw call. The fragment rotates its object space position into the liquid frame, so waterlines stay level while the glass turns. |
| Stream | `PourStream.cs` | Procedural strip mesh: short bezier off the lip, then a fall under gravity. |
| Rules | `WaterSortBoard.cs` | Integer stack per bottle. Move the whole matching run on top. |

## Numbers measured off the reference art

These are not taste, they were sampled from the source material and can be re-derived:

| Setting | Value | Where it came from |
| --- | --- | --- |
| `innerJunctionDepth` | 0.098 | junction sags 14px on a 143px chord |
| `maxCapDepth` | 0.075 | cap half depth against interior height |
| `brimHeadroom` | 0.34 | a full vessel never reaches its own brim |
| `visibleBottomShare` | 0.47 | where the outline stops hiding the liquid |
| `evenBandHeights` | 1 | players read pixel heights, not volumes |
