// Recolours a glass drawing's stroke from a scene theme, at draw time.
//
// This replaces GlassLineStyler's runtime repaint. That version read every pixel of a
// 2048x2048 PNG, built several full size bool/int/float/Color32 arrays and rasterised a
// new sprite — around 176MB of temporary allocation per call, repeated on every domain
// reload, and it forced Read/Write to stay on for the source texture.
//
// The drawing already carries its own tube shading: dark on the casing, bright along the
// core. So the stroke's own luminance is the ramp, and the theme supplies the two ends of
// it. Nothing is read back to the CPU and nothing is generated.
Shader "LiquidSort/GlassContour"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _ContourDark ("Contour Dark", Color) = (0.157, 0.196, 0.298, 1)
        _ContourLight ("Contour Light", Color) = (0.722, 0.780, 0.878, 1)
        // Where the drawing's own luminance is taken to be fully dark and fully lit.
        _RampLow ("Ramp Low", Range(0,1)) = 0.10
        _RampHigh ("Ramp High", Range(0,1)) = 0.62
        // Degrees. 0 = from the right, 90 = from above.
        _LightAngle ("Light Angle", Range(0,360)) = 115
        _LightStrength ("Light Strength", Range(0,1)) = 0.22
        // The source drawing already knows where its tiny rim/handle highlights are.
        // Preserve that artist placement and lift only its brightest authored pixels.
        _SpecularColor ("Authored Specular Color", Color) = (0.76,0.94,1,1)
        _SpecularStrength ("Authored Specular Strength", Range(0,1)) = 0.22
        _SpecularLow ("Authored Specular Low", Range(0,1)) = 0.58
        _SpecularHigh ("Authored Specular High", Range(0,1)) = 0.92
        // Optional per-material lift for an unusually dark authored mouth ring.
        // Zero is byte-for-byte the old look; only CocktailGlassContour enables it.
        _TopRimBoost ("Top Rim Brightness", Range(0,1)) = 0
        _TopRimY ("Top Rim Local Y", Float) = 10000
        _TopRimFeather ("Top Rim Feather", Range(0.001,1)) = 0.10
        [HideInInspector] _InteriorRect ("Interior Rect", Vector) = (-0.5,-0.5,0.5,0.5)
        [HideInInspector] _VisibleFloorY ("Optical Liquid Floor Y", Float) = -10000
        [HideInInspector] _VisibleBottomY ("Visible Liquid Bottom Y", Float) = -10000
        [HideInInspector] _ContactStrength ("Liquid/Glass Contact Lift", Range(0,1)) = 0
        [HideInInspector] _AccessoryFx ("Handle, Stem/Foot, Feather, Stem Toon", Vector) = (0,0,0.025,0)
        [HideInInspector] _BottomRimStrength ("Lower Silhouette Highlight", Range(0,1)) = 0
        [HideInInspector] _RimHotspotStrength ("Warm Upper-left Rim Hotspot", Range(0,1)) = 0
        [HideInInspector] _LiquidBounceColor ("Liquid Base Bounce", Color) = (0,0,0,0)
        [HideInInspector] _LiquidBounceStrength ("Liquid Base Bounce Strength", Range(0,1)) = 0
        [HideInInspector] _PaintedToyStrength ("Painted Toy Treatment", Range(0,1)) = 0
        [HideInInspector] _ToyMidColor ("Painted Toy Mid", Color) = (0.31,0.47,0.59,1)
        [HideInInspector] _ToyFillColor ("Painted Toy Cyan", Color) = (0.27,0.89,0.96,1)
        [HideInInspector] _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _ContourDark;
            fixed4 _ContourLight;
            float _RampLow;
            float _RampHigh;
            float _LightAngle;
            float _LightStrength;
            fixed4 _SpecularColor;
            float _SpecularStrength;
            float _SpecularLow;
            float _SpecularHigh;
            float _TopRimBoost;
            float _TopRimY;
            float _TopRimFeather;
            float4 _InteriorRect;
            float _VisibleFloorY;
            float _VisibleBottomY;
            float _ContactStrength;
            float4 _AccessoryFx;
            float _BottomRimStrength;
            float _RimHotspotStrength;
            fixed4 _LiquidBounceColor;
            float _LiquidBounceStrength;
            float _PaintedToyStrength;
            fixed4 _ToyMidColor;
            fixed4 _ToyFillColor;
            fixed4 _Color;
            fixed4 _RendererColor;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.local = v.vertex.xy;
                o.color = v.color * _Color * _RendererColor;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, i.uv);
                clip(src.a - (1.0 / 255.0));

                // Imported sprite textures are straight-alpha here. Dividing their RGB
                // by coverage drove antialiased edge texels towards white and produced a
                // pale halo on bright scenes. Alpha already controls coverage below; the
                // authored straight RGB is the correct contour luminance ramp.
                float lum = dot(src.rgb, float3(0.299, 0.587, 0.114));
                float ramp = smoothstep(_RampLow, _RampHigh, lum);

                float3 col = lerp(_ContourDark.rgb, _ContourLight.rgb, ramp);
                float2 interiorSize = max(_InteriorRect.zw - _InteriorRect.xy,
                                          float2(1e-4, 1e-4));
                float2 toyUv = (i.local - _InteriorRect.xy) / interiorSize;
                float2 vesselUv = saturate(toyUv);
                float toy = saturate(_PaintedToyStrength);

                // One scene light, fixed to the vessel art: warm from upper-left. It
                // gates authored specular placement; the opposite cyan edge remains in
                // the contour ramp and the separate ThinFX fill rather than turning the
                // complete outline warm.
                float a = radians(_LightAngle);
                float2 dir = float2(cos(a), sin(a));
                float facing = dot(normalize(i.local + float2(1e-5, 1e-5)), dir);
                float keyFacing = smoothstep(-0.15, 0.75, facing);

                // The cocktail drawing's mouth ellipse is much darker than its side
                // stroke. A local-y mask lets its own material open just that ring while
                // every other vessel keeps the shared contour appearance unchanged.
                float rim = smoothstep(_TopRimY - max(_TopRimFeather, 1e-4),
                                       _TopRimY + max(_TopRimFeather, 1e-4),
                                       i.local.y) * saturate(_TopRimBoost);
                col = lerp(col, _ContourLight.rgb,
                    rim * lerp(0.35, 1.0, keyFacing));

                // This is the crisp highlight layer the legacy full-silhouette wash could
                // not provide. It never enters the transparent interior: source alpha and
                // luminance place it only on the rim, side, base and handle pixels that the
                // vessel artist actually painted bright.
                float specular = smoothstep(_SpecularLow,
                    max(_SpecularHigh, _SpecularLow + 1e-4), lum);
                col = lerp(col, _SpecularColor.rgb,
                    saturate(specular * _SpecularStrength * keyFacing));

                // The side facing the light keeps a little more of the bright end. Cheap
                // stand-in for the outward normal the CPU version derived from an SDF.
                col *= 1.0 + _LightStrength * facing;

                // The premium-casual mouth treatment is deliberately hand-directed:
                // only authored pixels near the upper-left lip receive the creamy key.
                // The transparent cavity can never enter because source alpha remains
                // the final coverage mask.
                float topZone = smoothstep(0.78, 0.98, vesselUv.y);
                float leftZone = 1.0 - smoothstep(0.30, 0.72, vesselUv.x);
                float authoredHotspot = smoothstep(0.18, 0.64, lum);
                float rimHotspot = topZone * leftZone * authoredHotspot
                                 * keyFacing * saturate(_RimHotspotStrength);
                col = lerp(col, _SpecularColor.rgb, saturate(rimHotspot));

                // A handle and a cocktail stem are solid glass parts, not part of the
                // liquid cavity. Their profile selects the relevant region, then the
                // source drawing's own luminance selects the actual painted reflection.
                // No procedural colour is ever laid across the transparent cavity, and
                // the soft local-space gates cannot create a square highlight at the base.
                float feather = max(_AccessoryFx.z * interiorSize.x, 1e-4);
                float insideX = smoothstep(_InteriorRect.x - feather,
                                           _InteriorRect.x + feather, i.local.x)
                              * (1.0 - smoothstep(_InteriorRect.z - feather,
                                                  _InteriorRect.z + feather, i.local.x));
                float insideY = smoothstep(_InteriorRect.y - feather,
                                           _InteriorRect.y + feather, i.local.y)
                              * (1.0 - smoothstep(_InteriorRect.w - feather,
                                                  _InteriorRect.w + feather, i.local.y));
                float handleRegion = (1.0 - insideX) * insideY;
                float stemFootRegion = 1.0 - smoothstep(_InteriorRect.y - feather,
                                                       _InteriorRect.y + feather, i.local.y);
                float accessoryRegion = saturate(handleRegion * _AccessoryFx.x
                                               + stemFootRegion * _AccessoryFx.y);
                // Stemmed glass artwork often contains a long, continuous grey ramp.
                // That reads as physically rendered crystal beside the deliberately flat
                // puzzle liquids. Quantise only the solid stem and foot into three softly
                // antialiased colour bands; the profile blends this treatment in, so the
                // bowl and every non-stemmed vessel retain their authored shading.
                float stemToon = stemFootRegion * saturate(_AccessoryFx.w);
                float toonMiddle = smoothstep(0.27, 0.36, lum);
                float toonHighlight = smoothstep(0.64, 0.75, lum);
                float toonRamp = 0.16 + toonMiddle * 0.36 + toonHighlight * 0.30;
                float3 toonAccessory = lerp(_ContourDark.rgb, _ContourLight.rgb,
                                            saturate(toonRamp));
                // The painted blue assets live mostly in the middle of the luminance
                // range. Expand that authored ramp so its pale centre becomes the crisp
                // cyan-white reflection visible in the reference, while the navy edge
                // remains an edge instead of being uniformly bleached.
                float authoredAccessory = smoothstep(0.30, 0.58, lum);
                authoredAccessory = lerp(authoredAccessory,
                                         smoothstep(0.63, 0.72, lum), stemToon);
                col = lerp(col, _SpecularColor.rgb,
                    saturate(accessoryRegion * authoredAccessory));
                // Apply the flat palette after the broad authored glass reflection, so
                // it cannot reintroduce the metallic-looking continuous ramp. Retain one
                // small, hand-directed left patch as the premium-casual "toy shine" cue.
                col = lerp(col, toonAccessory, stemToon);
                float stemX = (i.local.x - (_InteriorRect.x + _InteriorRect.z) * 0.5)
                            / interiorSize.x;
                float toyShineStripe = smoothstep(-0.42, -0.24, stemX)
                                      * (1.0 - smoothstep(-0.10, 0.08, stemX));
                float toyShine = toyShineStripe * stemToon * 0.28
                               + smoothstep(0.80, 0.92, lum) * stemToon * 0.24;
                col = lerp(col, _SpecularColor.rgb, saturate(toyShine));

                UNITY_BRANCH
                if (toy > 0.001)
                {
                    // The source mug owns a long realistic blue-grey gradient. Flatten
                    // only its handle first, then paint three readable toy-light zones:
                    // cyan outer arc, plum inner/lower AO, and one short warm key.
                    // toyUv is deliberately unsaturated; x > 1 identifies the handle
                    // without catching the vessel's ordinary right wall.
                    float rightHandle = smoothstep(-0.015, 0.035, toyUv.x - 1.0)
                                      * smoothstep(0.18, 0.28, toyUv.y)
                                      * (1.0 - smoothstep(0.78, 0.86, toyUv.y));
                    col = lerp(col, _ToyMidColor.rgb,
                        saturate(rightHandle * toy * 0.90));

                    float handleX = (toyUv.x - 1.0) / 0.46;
                    float handleOuterCool = rightHandle
                        * smoothstep(0.42, 0.72, handleX)
                        * (1.0 - smoothstep(0.94, 1.05, handleX))
                        * smoothstep(0.27, 0.38, toyUv.y)
                        * (1.0 - smoothstep(0.70, 0.82, toyUv.y));
                    float3 coolHandle = lerp(
                        _ToyMidColor.rgb, _ToyFillColor.rgb, 0.48);
                    col = lerp(col, coolHandle,
                        saturate(handleOuterCool * toy * 0.82));

                    float handleInnerAo = rightHandle
                        * smoothstep(0.10, 0.24, handleX)
                        * (1.0 - smoothstep(0.62, 0.78, handleX))
                        * smoothstep(0.20, 0.29, toyUv.y)
                        * (1.0 - smoothstep(0.43, 0.56, toyUv.y));
                    col = lerp(col, _ContourDark.rgb,
                        saturate(handleInnerAo * toy * 0.82));

                    float handleWarmKey = rightHandle
                        * smoothstep(0.18, 0.34, handleX)
                        * (1.0 - smoothstep(0.73, 0.88, handleX))
                        * smoothstep(0.62, 0.69, toyUv.y)
                        * (1.0 - smoothstep(0.77, 0.84, toyUv.y));
                    col = lerp(col, _SpecularColor.rgb,
                        saturate(handleWarmKey * toy * 0.58));
                    // Tiny hard core inside the broad warm arc: enough to sell a glossy
                    // toy, far too small to turn the whole handle into white ceramic.
                    float handleWarmCore = rightHandle
                        * smoothstep(0.47, 0.56, handleX)
                        * (1.0 - smoothstep(0.66, 0.74, handleX))
                        * smoothstep(0.69, 0.73, toyUv.y)
                        * (1.0 - smoothstep(0.755, 0.79, toyUv.y));
                    col = lerp(col, _SpecularColor.rgb,
                        saturate(handleWarmCore * toy * 0.76));

                    // A broad left key occupies one painted wall region. A soft dip in
                    // the middle stops it becoming a mechanical full-height white line.
                    float leftWall = (1.0 - smoothstep(0.015, 0.105, toyUv.x))
                                   * smoothstep(0.16, 0.28, toyUv.y)
                                   * (1.0 - smoothstep(0.73, 0.86, toyUv.y));
                    float wallBreak = 1.0 - 0.24
                        * exp(-pow((toyUv.y - 0.49) / 0.085, 2.0));
                    float3 paintedKey = lerp(
                        _ToyMidColor.rgb, _SpecularColor.rgb, 0.64);
                    float leftPaint = leftWall * wallBreak * toy;
                    col = lerp(col, paintedKey, saturate(leftPaint * 0.68));
                    float leftWhiteCore = leftPaint * smoothstep(0.78, 0.90, lum);
                    col = lerp(col, _SpecularColor.rgb,
                        saturate(leftWhiteCore * 0.22));

                    // One short cream segment around ten o'clock. Most of the mouth
                    // ellipse remains blue/plum, exactly as in the toy references.
                    float toyLip = smoothstep(0.925, 0.972, toyUv.y)
                                 * (1.0 - smoothstep(1.00, 1.035, toyUv.y))
                                 * smoothstep(0.04, 0.10, toyUv.x)
                                 * (1.0 - smoothstep(0.34, 0.46, toyUv.x))
                                 * smoothstep(0.20, 0.52, lum);
                    col = lerp(col, _SpecularColor.rgb,
                        saturate(toyLip * toy * 0.90));
                }

                // The liquid-to-glass junction is not a directional highlight. Lighting
                // it from above leaves the authored navy row black; lighting it from
                // below turns the next row white. Instead, lift only dark painted glass
                // pixels between the measured optical floor and first visible liquid row
                // toward a neutral middle of the glass ramp. Source alpha supplies the
                // curved silhouette, so this cannot become a rectangular band or enter
                // the liquid. Placing it after directional/specular lighting also makes
                // the contact stable when the vessel rotates during a pour.
                float contactLow = min(_VisibleFloorY, _VisibleBottomY);
                float contactHigh = max(_VisibleFloorY, _VisibleBottomY);
                float contactFeather = max(interiorSize.x * 0.018, 1e-4);
                float contactWindow = smoothstep(contactLow - contactFeather * 2.0,
                                                 contactLow + contactFeather, i.local.y)
                                    * (1.0 - smoothstep(contactHigh + contactFeather,
                                                       contactHigh + contactFeather * 3.0,
                                                       i.local.y));
                float authoredDark = 1.0 - smoothstep(0.30, 0.70, lum);
                float3 contactGlass = lerp(_ContourDark.rgb, _ContourLight.rgb, 0.44);
                col = lerp(col, contactGlass,
                    saturate(contactWindow * authoredDark * _ContactStrength
                             * lerp(1.0, 0.55, toy)));

                // A toy base is a dark plum support, not a continuation of the source's
                // smooth grey-blue ramp. Apply this after the generic contact correction;
                // the localized liquid echo and thin cyan silhouette are painted later.
                float toyBaseZone = 1.0 - smoothstep(
                    contactLow - interiorSize.x * 0.10,
                    contactLow + interiorSize.x * 0.045, i.local.y);
                float3 toyBaseColor = lerp(
                    _ContourDark.rgb, _ToyMidColor.rgb, 0.18);
                col = lerp(col, toyBaseColor,
                    saturate(toyBaseZone * toy * 0.82));

                // The lowest liquid colour reflects through the nearby authored glass
                // base, including the small solid foot below the measured optical floor.
                // Source alpha and authored luminance retain the curved silhouette. The
                // outer cyan rim is applied afterwards, so the edge remains readable over
                // every puzzle colour.
                float bounceReach = interiorSize.x * 0.16;
                float baseBounceWindow =
                    smoothstep(contactLow - bounceReach,
                               contactLow - contactFeather, i.local.y)
                  * (1.0 - smoothstep(contactHigh + contactFeather,
                                      contactHigh + contactFeather * 3.0,
                                      i.local.y));
                // A cocktail stem begins directly under its bowl. Let the contact itself
                // catch the liquid hue, but taper the downward extension before it can
                // colour the whole stem and foot.
                float extensionStrength = lerp(0.85, 0.22,
                    saturate(_AccessoryFx.y));
                float regularBounceMask = max(contactWindow,
                                              baseBounceWindow * extensionStrength)
                                        * lerp(0.35, 1.0, authoredDark);
                // The toy mug receives one small upper-left hue echo instead of having
                // its whole base repainted by the liquid colour.
                float toyBounceX = smoothstep(0.10, 0.20, toyUv.x)
                                 * (1.0 - smoothstep(0.42, 0.54, toyUv.x));
                float toyBounceY = smoothstep(
                    contactLow - interiorSize.x * 0.08,
                    contactLow - interiorSize.x * 0.025, i.local.y)
                    * (1.0 - smoothstep(
                        contactLow + interiorSize.x * 0.025,
                        contactLow + interiorSize.x * 0.075, i.local.y));
                float toyBounceMask = toyBaseZone * toyBounceX * toyBounceY
                                    * lerp(0.45, 1.0, ramp);
                float bounceMask = lerp(regularBounceMask, toyBounceMask, toy);
                float3 bounceTarget = lerp(_LiquidBounceColor.rgb,
                                           _SpecularColor.rgb, toy * 0.10);
                col = lerp(col, bounceTarget,
                    saturate(bounceMask * _LiquidBounceStrength
                             * _LiquidBounceColor.a));

                // Brighten only the true outer alpha edge below the liquid cavity. Probe
                // just outside the current texel instead of using fwidth(src.a): a fully
                // opaque authored edge can have a zero alpha derivative on the final
                // surviving fragment. The probe follows the actual curved silhouette and
                // grows only to about one output pixel when the glass is shown on a small
                // order card. It cannot fill the base or form a rectangular floor band.
                float belowCavity = 1.0 - smoothstep(_InteriorRect.y - feather,
                                                     _InteriorRect.y + feather, i.local.y);
                float2 edgeProbe = max(_MainTex_TexelSize.xy * 1.5,
                                       fwidth(i.uv) * 0.82);
                float neighbourAlpha = min(
                    min(tex2D(_MainTex, i.uv + float2(edgeProbe.x, 0.0)).a,
                        tex2D(_MainTex, i.uv - float2(edgeProbe.x, 0.0)).a),
                    min(tex2D(_MainTex, i.uv + float2(0.0, edgeProbe.y)).a,
                        tex2D(_MainTex, i.uv - float2(0.0, edgeProbe.y)).a));
                float alphaEdge = smoothstep(0.04, 0.72,
                                             saturate(src.a - neighbourAlpha));
                float bottomRim = alphaEdge * belowCavity * saturate(_BottomRimStrength)
                                * lerp(0.68, 1.0, ramp);
                col = lerp(col, _ContourLight.rgb, saturate(bottomRim));

                // Opposite rim: a real 1-2 pixel alpha edge. This trims the right wall
                // and outer handle in cyan without laying another broad gradient over it.
                float rightProbe = max(_MainTex_TexelSize.x * 2.0,
                                       fwidth(i.uv.x) * 0.75);
                float alphaOutsideRight = tex2D(_MainTex,
                    i.uv + float2(rightProbe, 0.0)).a;
                float outerRightEdge = smoothstep(0.06, 0.48,
                    saturate(src.a - alphaOutsideRight));
                float toyRightRim = outerRightEdge
                    * smoothstep(0.90, 0.99, toyUv.x)
                    * smoothstep(0.12, 0.24, toyUv.y)
                    * (1.0 - smoothstep(0.86, 0.96, toyUv.y));
                col = lerp(col, _ToyFillColor.rgb,
                    saturate(toyRightRim * toy * 0.78));

                // The authored stem also fades its alpha like real transparent crystal.
                // Gently pack that coverage for the toon treatment: internal glass becomes
                // toy-solid while antialiased outer pixels stay soft and clean.
                float toyAlpha = 1.0 - pow(1.0 - saturate(src.a), 2.35);
                float alpha = lerp(src.a, toyAlpha, stemToon) * i.color.a;
                return fixed4(saturate(col) * alpha, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
