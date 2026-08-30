// Water/liquid sort style bottle contents.
//
// One draw call per bottle. The liquid is described by up to eight bands whose
// upper boundaries are horizontal *in world space*, so the waterlines stay level
// while the bottle rotates. The boundary between two bands is drawn as the near
// rim of an ellipse, which is what sells the "cylinder of liquid" look.
Shader "LiquidSort/BottleLiquid"
{
    Properties
    {
        _MaskTex ("Interior Mask", 2D) = "white" {}
        // Measured off the reference art: on a 143 px chord the top face runs 19.5 px
        // from the waterline up to its far rim, so the half depth is 0.136 of the chord.
        _Bulge ("Cap Depth / Chord", Range(0.02,0.20)) = 0.135
        // Absolute ceiling on cap depth. Without it a wide vessel (a coupe, a martini
        // glass) gets a top face as deep as its own bowl, because the cap scales with chord.
        _BulgeMax ("Cap Depth Ceiling", Float) = 0.16
        // The reference arcs its junctions by 20.3 px on that same 143 px chord, i.e.
        // the same depth as the top face, so each band reads as its own cylinder slice.
        // 0 flattens them into straight horizontal lines instead.
        _InnerCurve ("Inner Junction Curve", Range(0,1)) = 1.0
        // Measured separately from the cap, because they are not the same quantity.
        // Reference junction sags 14px on a 143px chord: 0.098 of the chord. On a narrow
        // bottle that happens to equal the cap depth, which is why one constant looked
        // like enough; on a wide bowl the cap has to stay shallow to leave room for the
        // colour while the junction still needs its full sag, or the bands read flat.
        _InnerBulge ("Junction Depth / Chord", Range(0,0.25)) = 0.098
        _InnerMax ("Junction Depth Ceiling", Float) = 0.36
        // Measured off the reference top faces: sand body B4B798 caps at C0BA91 and
        // orange E35800 caps at F78E11, both a lift of about 1.1 in value with the
        // saturation almost untouched. The old 1.88 came from a dark wine band, which
        // is the one liquid whose cap really is nearly twice its body, and applying
        // that to every colour blew pale liquids out to flat white.
        // Expressed that way rather than as gain plus offset because an offset blows
        // pale liquids out to flat white while this keeps their hue.
        _CapValue ("Top Face Value", Range(1,3)) = 1.22
        _CapDesat ("Top Face Desaturate", Range(0,1)) = 0.10
        _CapFalloff ("Top Face Far Shade", Range(0.4,1)) = 0.74
        _EdgeShade ("Edge Shade", Range(0,1)) = 0.06
        _BodyShade ("Depth Shade", Range(0,1)) = 0.10
        _DepthRange ("Depth Range", Float) = 2.0
        // Inner wall contact shade. _EdgeShade darkens by distance from the middle of
        // the *quad*, which is wrong the moment the vessel is a cone: the wall is not
        // where the quad ends. These three sample the interior mask a short way to the
        // side instead, so the shade tracks the real silhouette at every height.
        _WallShade ("Inner Wall Shade", Range(0,1)) = 0.20
        _WallWidth ("Inner Wall Width", Float) = 0.11
        // The key light is up and to the left, so the left wall catches far less shade.
        _WallBias ("Lit Side Wall Shade", Range(0,1)) = 0.45
        _FloorShade ("Floor Shade", Range(0,1)) = 0.24
        _BoundaryShade ("Boundary Depth", Range(0,1)) = 0.68
        _CapRim ("Cap Rim Light", Range(0,1)) = 0.32
        // One shot flash across the whole top face, raised when a pour lands.
        _CapFlash ("Top Face Flash", Range(0,1)) = 0.0
        // Lets the glint leave the 0..1 range so a bloom pass has something to find.
        // 1 keeps the old clamped output exactly.
        _Overbright ("Glint Overbright", Range(1,4)) = 1.0
        _Shine ("Top Glint", Range(0,1)) = 0.0
        _ShineX ("Top Glint X", Range(-1,1)) = -0.15
        _ShineWidth ("Top Glint Width", Range(0.01,1)) = 0.42
        _Wave ("Surface Wave", Float) = 0.0
        _WaveFreq ("Wave Frequency", Float) = 9.0
        _WaveSpeed ("Wave Speed", Float) = 7.0
        _Alpha ("Alpha", Range(0,1)) = 1.0
        _QuadSize ("Quad Size", Vector) = (1,1,0,0)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            #define MAX_BANDS 8

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
            };

            sampler2D _MaskTex;

            // (u0, v0, du, dv) sub rect of the mask texture used by this bottle.
            float4 _MaskUV;
            // rgb = band colour.
            float4 _BandColor[MAX_BANDS];
            // Top face per band. w is 1 when the colour was authored, 0 to derive it.
            float4 _BandCap[MAX_BANDS];
            // x = waterline height, y = chord centre, z = chord half width (liquid frame).
            float4 _BandInfo[MAX_BANDS];
            float _BandCount;
            // Rotation that maps object space into the liquid frame, in radians.
            float _Angle;
            float2 _Interior;

            float _Bulge;
            float _InnerCurve;
            float _InnerBulge;
            float _InnerMax;
            float _BulgeMax;
            float _CapValue;
            float _CapDesat;
            float _CapFalloff;
            float _EdgeShade;
            float _BodyShade;
            float _DepthRange;
            float _WallShade;
            float _WallWidth;
            float _WallBias;
            float _FloorShade;
            float _BoundaryShade;
            float _CapRim;
            float _CapFlash;
            float _Overbright;
            float _Shine;
            float _ShineX;
            float _ShineWidth;
            float _Wave;
            float _WaveFreq;
            float _WaveSpeed;
            float _Alpha;
            float2 _QuadSize;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.local = v.vertex.xy;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // The interior mask is sampled twice, further down: once where the
                // fragment is, for the flat bodies, and once lower, for the top face.
                // Clipping here would throw away the back half of the surface before it
                // is ever drawn, which is what used to slice the ellipse flat.

                // Object space -> liquid frame. The liquid frame is world aligned
                // (plus a small slosh angle), so ly is a real world height.
                float sa, ca;
                sincos(_Angle, sa, ca);
                float ly = i.local.x * sa + i.local.y * ca;
                float lx = i.local.x * ca - i.local.y * sa;

                int count = min(max((int)_BandCount, 0), MAX_BANDS);
                clip((float)count - 0.5);
                int topIndex = max(count - 1, 0);

                // Start with the bottom colour and replace it as each curved,
                // world-horizontal boundary is crossed. Derivatives soften the
                // transition by roughly one screen pixel instead of hard aliasing it.
                float3 bodyColor = _BandColor[0].rgb;
                float boundaryRim = 0.0;

                for (int k = 0; k < MAX_BANDS - 1; k++)
                {
                    if (k >= count - 1) break;

                    float4 info = _BandInfo[k];
                    float halfChord = max(info.z, 1e-4);
                    float chord = halfChord * 2.0;
                    float across = saturate(abs(lx - info.y) / halfChord);
                    float ellipse = sqrt(saturate(1.0 - across * across));
                    // Internal junctions use the near arc of the upper opaque slice.
                    float halfDepth = min(chord * max(_InnerBulge, 0.001), _InnerMax) * _InnerCurve;
                    float nearY = info.x - halfDepth * ellipse;
                    float signedBoundary = ly - nearY;
                    float aa = max(fwidth(signedBoundary) * 0.85, chord * 0.0015);
                    float crossed = smoothstep(-aa, aa, signedBoundary);

                    bodyColor = lerp(bodyColor, _BandColor[k + 1].rgb, crossed);

                    // A narrow shadow immediately below each colour junction gives
                    // the curved piece depth without shading the whole liquid body.
                    float rimWidth = max(aa * 2.25, chord * 0.009);
                    float belowRim = (1.0 - smoothstep(0.0, rimWidth, -signedBoundary))
                                   * (1.0 - crossed);
                    boundaryRim = max(boundaryRim, belowRim);
                }

                float4 topInfo = _BandInfo[topIndex];
                float topHalfChord = max(topInfo.z, 1e-4);
                float topChord = topHalfChord * 2.0;
                float topAcross = saturate(abs(lx - topInfo.y) / topHalfChord);
                float topEllipse = sqrt(saturate(1.0 - topAcross * topAcross));
                float topHalfDepth = min(topChord * max(_Bulge, 0.001), _BulgeMax);
                float wave = _Wave * sin(lx * _WaveFreq + _Time.y * _WaveSpeed);
                float topCentre = topInfo.x + wave;
                float nearTop = topCentre - topHalfDepth * topEllipse;
                float farTop = topCentre + topHalfDepth * topEllipse;

                float outerDistance = ly - farTop;
                float outerAA = max(fwidth(outerDistance), topChord * 0.0015);
                float liquidCoverage = 1.0 - smoothstep(-outerAA, outerAA, outerDistance);

                float surfaceDistance = ly - nearTop;
                float surfaceAA = max(fwidth(surfaceDistance) * 0.85, topChord * 0.0015);
                float surface = smoothstep(-surfaceAA, surfaceAA, surfaceDistance);

                // Flat bodies keep the interior mask exactly. The top face does not:
                // the vessel's mouth is open, so its back rim belongs above the interior
                // outline, tucked under the drawn glass rim. Sampling the mask at the
                // near rim rather than at the fragment extrudes the waterline chord
                // upwards, which is precisely the silhouette that half of the ellipse
                // needs, and it costs one extra texture read instead of a second mask.
                float bodyMask = tex2D(_MaskTex, _MaskUV.xy + i.uv * _MaskUV.zw).a;

                float rise = max(0.0, ly - nearTop);
                float2 shift = float2(-rise * sa, -rise * ca) / max(_QuadSize, float2(1e-4, 1e-4));
                float surfaceMask = tex2D(_MaskTex, _MaskUV.xy + (i.uv + shift) * _MaskUV.zw).a;

                float mask = lerp(bodyMask, surfaceMask, surface);
                clip(mask - (1.0 / 255.0));

                // Contact shade against the glass wall and against the floor. Three more
                // taps of the same interior mask, one to each side and one below: where
                // the neighbouring sample already falls outside the vessel this fragment
                // is up against the wall, whatever shape that wall has at this height.
                // Unlike _EdgeShade this needs no assumption that the vessel is a box.
                float2 quadSize = max(_QuadSize, float2(1e-4, 1e-4));
                float2 sideStep = float2(_WallWidth, 0.0) / quadSize;
                float2 floorStep = float2(0.0, _WallWidth * 1.5) / quadSize;
                float openLeft = tex2D(_MaskTex, _MaskUV.xy + (i.uv - sideStep) * _MaskUV.zw).a;
                float openRight = tex2D(_MaskTex, _MaskUV.xy + (i.uv + sideStep) * _MaskUV.zw).a;
                float openBelow = tex2D(_MaskTex, _MaskUV.xy + (i.uv - floorStep) * _MaskUV.zw).a;
                float wallAO = saturate((1.0 - openRight) + (1.0 - openLeft) * _WallBias);
                float floorAO = saturate(1.0 - openBelow);

                float2 n = i.local / max(_Interior, float2(1e-4, 1e-4));
                float edge = saturate(abs(n.x));
                float depth = saturate((topInfo.x - ly) / max(_DepthRange, 1e-4));

                // The body stays opaque and almost flat. There is deliberately no
                // vertical shine shared by all bands.
                bodyColor *= 1.0 - _BodyShade * depth;
                bodyColor *= 1.0 - _EdgeShade * edge * edge;
                bodyColor *= 1.0 - _BoundaryShade * boundaryRim;

                float3 topColor = _BandColor[topIndex].rgb;
                // Lift the band colour by value, not by adding a constant: divide out its
                // brightness, desaturate a little, then scale back up and clamp.
                float topValue = max(topColor.r, max(topColor.g, topColor.b));
                float3 topHue = topValue > 1e-4 ? topColor / topValue : topColor;
                float3 derivedCap = saturate(
                    lerp(topHue, float3(1.0, 1.0, 1.0), _CapDesat) * min(1.0, topValue * _CapValue));
                // An authored top face wins. The reference picks one per liquid rather
                // than deriving it, and a derived cap vanishes on a liquid that is
                // already near full brightness: our pink sat at V 0.97, every multiply
                // clamped straight back to the body, and the band lost its cap.
                float4 authoredCap = _BandCap[topIndex];
                float3 surfaceColor = lerp(derivedCap, authoredCap.rgb, authoredCap.w);
                surfaceColor *= 1.0 - (_EdgeShade * 0.30) * edge * edge;

                // Bright near lip and a short warm glint live only on the top face,
                // so neither can read as a broad front-glass reflection.
                float capSpan = max(farTop - nearTop, surfaceAA * 2.0);
                float capT = saturate(surfaceDistance / capSpan);

                // Across the top face the reference falls from full brightness on the
                // near lip to roughly three quarters of it at the far rim.
                surfaceColor *= lerp(1.0, _CapFalloff, capT);

                float capRim = (1.0 - smoothstep(0.0, 0.10, capT)) * surface;
                float3 warmWhite = float3(1.0, 0.97, 0.94);
                surfaceColor = lerp(surfaceColor, warmWhite,
                    saturate(_CapRim * capRim + _CapFlash * surface));

                // _ShineX and _ShineWidth are now chord-local top-glint controls.
                float capX = (lx - topInfo.y) / topHalfChord;
                float glintX = saturate(1.0 - abs(capX - _ShineX) / max(_ShineWidth, 1e-4));
                float glintY = saturate(1.0 - abs(capT - 0.34) / 0.085);
                float glint = glintX * glintX * glintY * glintY * surface;
                surfaceColor = lerp(surfaceColor, warmWhite, saturate(_Shine * glint));
                // Only the glint is allowed out of range, and only when asked. Everything
                // else stays inside 0..1 so the liquid never blooms as a whole slab.
                surfaceColor *= 1.0 + (_Overbright - 1.0) * glint;

                float3 c = lerp(bodyColor, surfaceColor, surface);

                // The top face is a surface, not a wall, so it takes a fraction of the
                // contact shade; the floor shade belongs to the body alone.
                c *= 1.0 - _WallShade * wallAO * lerp(1.0, 0.35, surface);
                c *= 1.0 - _FloorShade * floorAO * (1.0 - surface);

                float alpha = mask * liquidCoverage * _Alpha;
                clip(alpha - (1.0 / 255.0));
                return half4(max(c, 0.0), alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
