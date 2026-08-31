// Water/liquid sort style bottle contents.
//
// One draw call per bottle. The liquid is described by up to eight bands whose
// upper boundaries are horizontal *in world space*, so the waterlines stay level
// while the bottle rotates. Every cumulative liquid unit owns a fixed convex crest.
// A covered run keeps that one shared curved silhouette without drawing a second
// exposed disc, gap or outline between colours.
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
        // Every colour run remains a rounded liquid slice after another colour lands on it.
        // One shared curve avoids a gap, dark seam or two overlapping ellipses.
        _InnerCurve ("Inner Junction Curve", Range(0,1)) = 1.0
        _SurfaceScale ("Exposed Surface Depth Scale", Range(0,1)) = 1.0
        // Authored in pixels, but pixels of ROYAL's framing, not of whatever screen this
        // ends up on. _RoyalUnitsPerPixel carries how much vessel-local length one such
        // pixel stood for; the vessel publishes it from its own profile, so the inset is
        // the same share of the glass at every board scale and every device resolution.
        // Zero falls back to the old screen-derivative path for unpublished materials.
        _CapWallInset ("Top Face Wall Inset (Royal pixels)", Range(0,3)) = 1.25
        _RoyalUnitsPerPixel ("Royal Local Units Per Pixel", Float) = 0
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
        // A pale liquid has no headroom left above it, so its top face separates by
        // losing saturation instead of by gaining brightness. The body is never touched:
        // in the reference art it is exactly the colour that was authored, at every
        // height, and a band the player has to recognise cannot drift with the lighting.
        _CapDesatPale ("Top Face Desaturate (pale)", Range(0,1)) = 0.45
        // How far the far rim keeps its lift: 1 stays fully lit, 0 sinks back to the
        // band colour. Measured off the reference, the far rim sits a little under half
        // way between the two.
        _CapFalloff ("Top Face Far Shade", Range(0,1)) = 0.47
        _EdgeShade ("Edge Shade", Range(0,1)) = 0.0
        // Broad, asymmetric key/shade pair that makes each band read as liquid inside
        // one cylindrical vessel. These use glass-local X, so the lighting stays attached
        // to the cup while the liquid frame remains world-horizontal during a pour.
        _CylinderKey ("Cylinder Key", Range(0,1)) = 0.0
        _CylinderShade ("Cylinder Shade", Range(0,0.5)) = 0.0
        // Shared stage key for this unlit liquid. It is deliberately strongest on the
        // horizontal top face and only a quiet fill on the body, which gives the scene
        // one overhead source without flattening every authored gameplay colour.
        _OverheadColor ("Scene Overhead Light", Color) = (1.0,0.97,0.91,1)
        _OverheadStrength ("Scene Overhead Strength", Range(0,0.30)) = 0.0
        _BodyShade ("Depth Shade", Range(0,1)) = 0.0
        _DepthRange ("Depth Range", Float) = 2.0
        // Inner wall contact shade. _EdgeShade darkens by distance from the middle of
        // the *quad*, which is wrong the moment the vessel is a cone: the wall is not
        // where the quad ends. These three sample the interior mask a short way to the
        // side instead, so the shade tracks the real silhouette at every height.
        _WallShade ("Inner Wall Shade", Range(0,1)) = 0.10
        _WallWidth ("Inner Wall Width", Float) = 0.05
        // The key light is up and to the left, so the left wall catches far less shade.
        _WallBias ("Lit Side Wall Shade", Range(0,1)) = 0.45
        _FloorShade ("Floor Shade", Range(0,1)) = 0.0
        // Bounce off the floor of the vessel. Not in the reference art, which is flat to
        // the last pixel; it is here because a dark liquid reads better with a little
        // light under it. Value based, so it shows on a bordeaux and stays invisible on
        // a pale sand without needing a per colour switch.
        // Volume shading, gated by how much lift the colour had room for. A dark
        // liquid gets its roundness from the lit face and needs none of this; a pale one
        // has no headroom left above it and would read flat without it.
        _RoundShade ("Volume Shade", Range(0,1)) = 0.0
        // Where the cap cannot climb, the body settles instead, so the top face still
        // separates from what is under it.
        _BodySettle ("Body Settle", Range(0.5,1)) = 1.0
        // Kept as an optional look control, but off by default: a floor bounce sits
        // directly against authored base pixels and can read as liquid light leaking
        // onto the glass artwork. Surface/cylinder lighting remains liquid-only.
        _FloorGlow ("Floor Glow", Range(0,1)) = 0.0
        _FloorGlowWidth ("Floor Glow Width", Float) = 0.26
        // Optional contact depth where two colours meet. Zero keeps the original
        // seamless stack; a small value gives toy-like layers a narrow painted lip
        // without opening a transparent gap or adding another liquid surface.
        _BoundaryShade ("Boundary Depth", Range(0,1)) = 0.0
        _CapRim ("Cap Rim Light", Range(0,1)) = 0.25
        _FarRim ("Far Meniscus Light", Range(0,1)) = 0.0
        // Retained as an optional material art control. Runtime contact FX deliberately
        // leaves this at zero: a landing is local and must not bleach the whole surface.
        _CapFlash ("Top Face Flash", Range(0,1)) = 0.0
        // Lets the glint leave the 0..1 range so a bloom pass has something to find.
        // 1 keeps the old clamped output exactly.
        _Overbright ("Glint Overbright", Range(1,4)) = 1.0
        _Shine ("Top Glint", Range(0,1)) = 0.0
        _ShineX ("Top Glint X", Range(-1,1)) = -0.15
        _ShineWidth ("Top Glint Width", Range(0.01,1)) = 0.42
        _Wave ("Surface Wave", Float) = 0.0
        // Landing splash: a compact crown where the stream hits, settled in 0.18 s.
        // The peak stands roughly 10–15% of the surface span proud of it.
        _SplashAmp ("Splash Amount", Float) = 0.0
        _SplashX ("Splash Position", Float) = 0.0
        _SplashWidth ("Splash Width", Float) = 0.30
        _SplashLife ("Splash Normalized Life", Range(0,1)) = 0.0
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
            float _SurfaceScale;
            float _CapWallInset;
            float _RoyalUnitsPerPixel;
            float _InnerBulge;
            float _InnerMax;
            float _BulgeMax;
            float _CapValue;
            float _CapDesat;
            float _CapDesatPale;
            float _CapFalloff;
            float _EdgeShade;
            float _CylinderKey;
            float _CylinderShade;
            fixed4 _OverheadColor;
            float _OverheadStrength;
            float _BodyShade;
            float _DepthRange;
            float _WallShade;
            float _WallWidth;
            float _WallBias;
            float _FloorShade;
            float _RoundShade;
            float _BodySettle;
            float _FloorGlow;
            float _FloorGlowWidth;
            float _BoundaryShade;
            float _CapRim;
            float _FarRim;
            float _CapFlash;
            float _Overbright;
            float _Shine;
            float _ShineX;
            float _ShineWidth;
            float _Wave;
            float _SplashAmp;
            float _SplashX;
            float _SplashWidth;
            float _SplashLife;
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

            /// The lighting model for a lit liquid face: same hue, lifted in value and
            /// eased off saturation. Shared by the top cap and the floor bounce so the two
            /// read as one light source rather than two unrelated tints.
            float3 LitFace(float3 c)
            {
                float v = max(c.r, max(c.g, c.b));
                float3 hue = v > 1e-4 ? c / v : c;

                // How much of the wanted lift the white ceiling allows. A dark colour gets
                // all of it and needs nothing else; a pale one gets almost none, and pays
                // for the separation in saturation rather than in brightness.
                float wanted = v * max(_CapValue - 1.0, 1e-4);
                float got = min(1.0, v * _CapValue) - v;
                float headroom = saturate(got / max(wanted, 1e-4));
                float desat = lerp(_CapDesatPale, _CapDesat, headroom);

                return saturate(lerp(hue, float3(1.0, 1.0, 1.0), desat) * min(1.0, v * _CapValue));
            }

            // Analytic, anti-aliased drop. Contact droplets live in this same liquid draw
            // call; no particles, meshes, GameObjects or per-pour allocations are needed.
            float DropCoverage(float2 p, float2 centre, float radius)
            {
                float distanceToEdge = length(p - centre) - radius;
                float aa = max(fwidth(distanceToEdge), radius * 0.12);
                return 1.0 - smoothstep(-aa, aa, distanceToEdge);
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

                // A covered run is not another fully exposed top face: the liquid above
                // covers it. Its volume survives as one convex, world-horizontal crest.
                // Drawing a complete lit ellipse here makes separate floating pucks; using
                // only the far arc keeps one continuous stack and preserves the lower
                // liquid's bombelik after another colour lands on it.
                float3 bodyColor = _BandColor[0].rgb;
                float4 firstCap = _BandCap[0];
                float3 bodyKeyColor = lerp(LitFace(bodyColor), firstCap.rgb, firstCap.w);
                float boundaryContact = 0.0;

                for (int k = 0; k < MAX_BANDS - 1; k++)
                {
                    if (k >= count - 1) break;

                    float4 info = _BandInfo[k];
                    float halfChord = max(info.z, 1e-4);
                    float chord = halfChord * 2.0;
                    float across = saturate(abs(lx - info.y) / halfChord);
                    float ellipse = sqrt(saturate(1.0 - across * across));
                    // The interface between two colours is a horizontal disc hidden under
                    // the liquid above it, so what shows is the NEAR half of that disc:
                    // lowest in the middle of the vessel, rising to the wall. The upper
                    // colour presses down into the lower one, which is what makes it read
                    // as the dominant slab. A plus sign here draws the far rim instead —
                    // the shape a surface only has while nothing covers it — and the lower
                    // colour bulges up through the upper one. The shared boundary keeps
                    // its separately authored depth when another colour covers it.
                    float halfDepth = min(
                        chord * max(_InnerBulge, 0.001), _InnerMax) * _InnerCurve;
                    float nearY = info.x - halfDepth * ellipse;
                    float signedBoundary = ly - nearY;
                    float aa = max(fwidth(signedBoundary) * 0.85, chord * 0.0015);
                    float crossed = smoothstep(-aa, aa, signedBoundary);

                    // Keep the colours physically touching, but retain a very narrow
                    // contact shadow below their curved handoff when the material asks
                    // for it. This is evaluated for every covered boundary, so orange,
                    // green and pink each keep their own readable slab instead of
                    // merging into one tall multicolour block.
                    //
                    // The width is chord-based so it stays consistent across vessel
                    // shapes, and the aa term is only a legibility FLOOR: a shadow thinner
                    // than its own antialiasing is a grey smear. Left uncapped that floor
                    // took over the moment the glass shrank - aa is a constant screen
                    // width, so on a three-row shelf glass it drew a shadow 2.6x thicker
                    // against a band 2.6x shorter, and two units read as one. Capping it
                    // at twice the authored share keeps the floor doing its job without
                    // ever letting it become the rule.
                    float contactWidth = max(chord * 0.009,
                                             min(aa * 2.5, chord * 0.018));
                    float contactBand = 1.0 - smoothstep(aa * 0.35,
                        contactWidth, abs(signedBoundary));
                    boundaryContact = max(boundaryContact, contactBand);

                    float3 nextBodyColor = _BandColor[k + 1].rgb;
                    float4 nextCap = _BandCap[k + 1];
                    float3 nextKeyColor = lerp(LitFace(nextBodyColor), nextCap.rgb, nextCap.w);
                    bodyColor = lerp(bodyColor, nextBodyColor, crossed);
                    bodyKeyColor = lerp(bodyKeyColor, nextKeyColor, crossed);
                }

                float4 topInfo = _BandInfo[topIndex];
                float topHalfChord = max(topInfo.z, 1e-4);
                float topChord = topHalfChord * 2.0;

                // BandInfo keeps the exact baked chord because it is part of the volume
                // calculation. Only the exposed face is pulled away from the glass.
                //
                // The inset must be a fixed share of THIS GLASS, which is what Royal shows
                // and what the volume law assumes. A screen derivative gives the opposite:
                // lx is object space, so ddx(lx) grows as the vessel shrinks on screen, and
                // the same authored 1.25px ate 0.99% of a Royal beer's top chord but 3.91%
                // of the same beer on a three-row shelf - and a different share again on
                // the next device resolution. _RoyalUnitsPerPixel is the profile's own
                // constant, so the eroded share is identical everywhere. The derivative
                // path remains only for a material nothing has published to.
                float localUnitsPerPixel = _RoyalUnitsPerPixel > 0.0
                    ? _RoyalUnitsPerPixel
                    : length(float2(ddx(lx), ddy(lx)));
                float capWallInset = min(
                    localUnitsPerPixel * max(_CapWallInset, 0.0),
                    topHalfChord * 0.15);
                float capHalfChord = max(topHalfChord - capWallInset, 1e-4);
                float capEdgeDistance = capHalfChord - abs(lx - topInfo.y);
                float capEdgeAA = max(
                    fwidth(capEdgeDistance) * 0.75,
                    topChord * 0.0015);
                float capCoverage = smoothstep(
                    -capEdgeAA, capEdgeAA, capEdgeDistance);

                float topAcross = saturate(abs(lx - topInfo.y) / capHalfChord);
                float topEllipse = sqrt(saturate(1.0 - topAcross * topAcross));
                float topHalfDepth = min(topChord * max(_Bulge, 0.001), _BulgeMax)
                                   * saturate(_SurfaceScale);
                float wave = _Wave * sin(lx * _WaveFreq + _Time.y * _WaveSpeed);

                float splash = 0.0;
                float dropletCoverage = 0.0;

                // _SplashAmp is one uniform value for the complete draw. Hinting the
                // branch therefore lets an idle vessel skip every contact exp, sin,
                // length and fwidth instead of paying for invisible zero-strength FX.
                UNITY_BRANCH
                if (_SplashAmp > 1e-5)
                {
                    // A real contact is not one broad jelly hump. Three compact lobes form
                    // a tiny crown under the stream, with deliberately unequal shoulders.
                    float splashT = (lx - _SplashX) / max(_SplashWidth, 1e-3);
                    float crownCentre = exp(-splashT * splashT * 4.4);
                    float crownLeftT = (splashT + 0.62) / 0.31;
                    float crownRightT = (splashT - 0.52) / 0.29;
                    float crownLeft = 0.58 * exp(-crownLeftT * crownLeftT);
                    float crownRight = 0.72 * exp(-crownRightT * crownRightT);
                    splash = _SplashAmp
                           * (crownCentre + crownLeft + crownRight) * 0.62;

                    // Three tiny ballistic beads peel away from the crown, then disappear
                    // before the 0.18 s contact beat ends. Their positions are a pure
                    // function of landing X and normalized life, so replay is deterministic.
                    float impactLife = saturate(_SplashLife);
                    float splashGate = saturate(
                        _SplashAmp / max(topChord * 0.018, 1e-4));
                    float2 liquidPoint = float2(lx, ly);
                    float dropBaseY = topInfo.x + topHalfDepth + _SplashAmp * 0.46;

                    float phaseA = saturate((impactLife - 0.02) / 0.68);
                    float windowA = smoothstep(0.02, 0.10, impactLife)
                                  * (1.0 - smoothstep(0.68, 0.82, impactLife));
                    float2 centreA = float2(
                        _SplashX - topChord * (0.046 + 0.040 * phaseA),
                        dropBaseY + topChord * (0.012
                            + 0.072 * sin(phaseA * UNITY_PI)));
                    float dropA = DropCoverage(
                        liquidPoint, centreA, topChord * 0.016) * windowA;

                    float phaseB = saturate((impactLife - 0.06) / 0.70);
                    float windowB = smoothstep(0.06, 0.15, impactLife)
                                  * (1.0 - smoothstep(0.74, 0.88, impactLife));
                    float2 centreB = float2(
                        _SplashX + topChord * (0.044 + 0.054 * phaseB),
                        dropBaseY + topChord * (0.018
                            + 0.086 * sin(phaseB * UNITY_PI)));
                    float dropB = DropCoverage(
                        liquidPoint, centreB, topChord * 0.013) * windowB;

                    float phaseC = saturate((impactLife - 0.12) / 0.64);
                    float windowC = smoothstep(0.12, 0.20, impactLife)
                                  * (1.0 - smoothstep(0.70, 0.92, impactLife));
                    float2 centreC = float2(
                        _SplashX + topChord * (0.008 + 0.018 * phaseC),
                        dropBaseY + topChord * (0.028
                            + 0.058 * sin(phaseC * UNITY_PI)));
                    float dropC = DropCoverage(
                        liquidPoint, centreC, topChord * 0.010) * windowC;

                    dropletCoverage = max(dropA, max(dropB, dropC)) * splashGate;
                }

                wave += splash;
                float topCentre = topInfo.x + wave;
                float nearTop = topCentre - topHalfDepth * topEllipse;
                float farTop = topCentre + topHalfDepth * topEllipse;

                float outerDistance = ly - farTop;
                float outerAA = max(fwidth(outerDistance), topChord * 0.0015);
                float liquidCoverage = 1.0 - smoothstep(-outerAA, outerAA, outerDistance);

                float surfaceDistance = ly - nearTop;
                float surfaceAA = max(fwidth(surfaceDistance) * 0.85, topChord * 0.0015);
                float surface = smoothstep(-surfaceAA, surfaceAA, surfaceDistance);
                surface *= capCoverage;

                // Flat bodies keep the interior mask exactly. The top face does not:
                // the vessel's mouth is open, so its back rim belongs above the interior
                // outline, tucked under the drawn glass rim. Sampling the mask at the
                // near rim rather than at the fragment extrudes the waterline chord
                // upwards, which is precisely the silhouette that half of the ellipse
                // needs. Two neighbouring taps erode only that exposed face by the
                // screen-stable inset; the body still reaches the physical inner wall.
                float2 quadSize = max(_QuadSize, float2(1e-4, 1e-4));
                float bodyMask = tex2D(_MaskTex, _MaskUV.xy + i.uv * _MaskUV.zw).a;

                float rise = max(0.0, ly - nearTop);
                float2 shift = float2(-rise * sa, -rise * ca) / quadSize;
                float2 surfaceUv = i.uv + shift;
                float2 capInsetUv = float2(ca, -sa) * capWallInset / quadSize;
                float surfaceMask = tex2D(
                    _MaskTex, _MaskUV.xy + surfaceUv * _MaskUV.zw).a;
                float surfaceMaskLeft = tex2D(
                    _MaskTex, _MaskUV.xy + (surfaceUv - capInsetUv) * _MaskUV.zw).a;
                float surfaceMaskRight = tex2D(
                    _MaskTex, _MaskUV.xy + (surfaceUv + capInsetUv) * _MaskUV.zw).a;
                surfaceMask = min(surfaceMask, min(surfaceMaskLeft, surfaceMaskRight));

                float mask = lerp(bodyMask, surfaceMask, surface);
                clip(mask - (1.0 / 255.0));

                // Contact shade against the glass wall and against the floor. Three more
                // taps of the same interior mask, one to each side and one below: where
                // the neighbouring sample already falls outside the vessel this fragment
                // is up against the wall, whatever shape that wall has at this height.
                // Unlike _EdgeShade this needs no assumption that the vessel is a box.
                float2 sideStep = float2(_WallWidth, 0.0) / quadSize;
                float2 floorStep = float2(0.0, _WallWidth * 1.5) / quadSize;
                float openLeft = tex2D(_MaskTex, _MaskUV.xy + (i.uv - sideStep) * _MaskUV.zw).a;
                float openRight = tex2D(_MaskTex, _MaskUV.xy + (i.uv + sideStep) * _MaskUV.zw).a;
                float openBelow = tex2D(_MaskTex, _MaskUV.xy + (i.uv - floorStep) * _MaskUV.zw).a;
                float wallAO = saturate((1.0 - openRight) + (1.0 - openLeft) * _WallBias);
                float floorAO = saturate(1.0 - openBelow);

                // Two taps at different reaches, so the bounce falls off instead of being
                // the hard one pixel band a single tap gives.
                float2 glowStep = float2(0.0, max(_FloorGlowWidth, 1e-4)) / quadSize;
                float glowNear = tex2D(_MaskTex, _MaskUV.xy + (i.uv - glowStep * 0.45) * _MaskUV.zw).a;
                float glowFar = tex2D(_MaskTex, _MaskUV.xy + (i.uv - glowStep) * _MaskUV.zw).a;
                float floorGlow = saturate((1.0 - glowFar) * 0.55 + (1.0 - glowNear) * 0.45);
                floorGlow *= floorGlow;

                float2 n = i.local / max(_Interior, float2(1e-4, 1e-4));
                float edge = saturate(abs(n.x));
                float depth = saturate((topInfo.x - ly) / max(_DepthRange, 1e-4));

                // A broad key left of centre and a restrained shade on the right make
                // separate colour bands belong to one cylindrical volume. It is lighting,
                // not a white stripe: each band moves only towards its own authored cap
                // colour, so gameplay colours remain recognisable.
                float cylinderKey = 1.0 - smoothstep(0.0, 0.62, abs(n.x + 0.38));
                float cylinderShade = smoothstep(-0.05, 0.95, n.x);
                cylinderShade *= cylinderShade;
                bodyColor = lerp(bodyColor, bodyKeyColor, _CylinderKey * cylinderKey);
                bodyColor *= 1.0 - _CylinderShade * cylinderShade;

                // Bodies stay opaque and meet directly. Boundary depth is an optional
                // material art control; its default of zero preserves the original look.
                bodyColor *= 1.0 - _BodyShade * depth;
                bodyColor *= 1.0 - _EdgeShade * edge * edge;
                bodyColor *= 1.0 - _BoundaryShade * boundaryContact;

                float3 topColor = _BandColor[topIndex].rgb;
                // Lift the band colour by value, not by adding a constant: divide out its
                // brightness, desaturate a little, then scale back up and clamp.
                float3 derivedCap = LitFace(topColor);
                // An authored top face wins. The reference picks one per liquid rather
                // than deriving it, and a derived cap vanishes on a liquid that is
                // already near full brightness: our pink sat at V 0.97, every multiply
                // clamped straight back to the body, and the band lost its cap.
                float4 authoredCap = _BandCap[topIndex];
                float3 surfaceColor = lerp(derivedCap, authoredCap.rgb, authoredCap.w);
                surfaceColor *= 1.0 - (_EdgeShade * 0.30) * edge * edge;
                surfaceColor *= 1.0 - (_CylinderShade * 0.25) * cylinderShade;

                // Bright near lip and a short warm glint live only on the top face,
                // so neither can read as a broad front-glass reflection.
                // Same rule as the contact shadow: the aa term guards a degenerate span,
                // it is not the span. Uncapped it widened the wall band where the top
                // face pinches out, so the near-rim light, far rim and glint all stopped
                // further from the glass on a shrunk vessel than on the Royal one.
                float capSpan = max(farTop - nearTop,
                                    min(surfaceAA * 2.0, topHalfDepth * 0.5 + 1e-5));
                float capT = saturate(surfaceDistance / capSpan);

                // Across the top face the light falls off towards the far rim, but it
                // must never fall past the band's own colour. A plain multiply did: on a
                // pale liquid, body (232,117,42) against a far rim of (189,112,64) put
                // the far side *darker* than what sits underneath it, so the highlight
                // read as a dent. Falling towards the band colour instead keeps the top
                // face lighter than its body for every colour in the palette.
                surfaceColor = lerp(surfaceColor, lerp(topColor, surfaceColor, _CapFalloff), capT);

                float capRim = (1.0 - smoothstep(0.0, 0.10, capT)) * surface;
                float3 warmWhite = float3(1.0, 0.97, 0.94);
                surfaceColor = lerp(surfaceColor, warmWhite,
                    saturate(_CapRim * capRim + _CapFlash * surface));

                // A second, quieter band on the far half keeps the top face reading as
                // a glossy liquid disc instead of a flat colour cut. It remains inside
                // the surface mask and never alters the fill geometry or waterline math.
                float farRim = saturate(1.0 - abs(capT - 0.88) / 0.10);
                farRim *= farRim * surface;
                surfaceColor = lerp(surfaceColor, warmWhite,
                    saturate(_FarRim * farRim));

                // _ShineX and _ShineWidth are now chord-local top-glint controls.
                float capX = (lx - topInfo.y) / capHalfChord;
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
                // Roundness is a ratio between the lit face and the body, and the lift
                // alone cannot always deliver it. Value 0.93 times 1.88 clamps at white,
                // so a pale liquid separates by 1.07 where a bordeaux separates by 1.80
                // and only the bordeaux looks like a cylinder. Measure what the ceiling
                // actually allowed and take the rest out of the body instead, so volume
                // survives at every brightness — and a dark liquid, which needs none of
                // it, never picks the black tones back up.
                float bodyValue = max(c.r, max(c.g, c.b));
                float wantedLift = bodyValue * max(_CapValue - 1.0, 1e-4);
                float gotLift = min(1.0, bodyValue * _CapValue) - bodyValue;
                float headroom = saturate(gotLift / max(wantedLift, 1e-4));
                float roundShade = _RoundShade * (1.0 - headroom);

                // The ellipse on top is what reads as roundness, and it needs contrast
                // against the body to be seen at all. A pale liquid cannot supply it from
                // above — the cap is already at white — so the body settles by the same
                // shortfall and the separation comes from below the line instead.
                c *= 1.0 - (1.0 - _BodySettle) * (1.0 - headroom) * (1.0 - surface);

                c *= 1.0 - roundShade * wallAO * lerp(1.0, 0.35, surface);
                c *= 1.0 - roundShade * 0.70 * floorAO * (1.0 - surface);

                c *= 1.0 - _FloorShade * floorAO * (1.0 - surface);
                c = lerp(c, LitFace(c), saturate(_FloorGlow * floorGlow) * (1.0 - surface));

                // The shelf scene is made from unlit sprites, so a Unity Light cannot
                // reach this draw call. Recreate the shared overhead key in headroom:
                // the exposed horizontal face receives the source, while the vertical
                // body gets just enough ambient spill to belong to the same scene.
                float overhead = saturate(_OverheadStrength)
                               * lerp(0.18, 1.0, surface);
                c += (1.0 - saturate(c)) * _OverheadColor.rgb * overhead;

                float alpha = mask * max(liquidCoverage, dropletCoverage) * _Alpha;
                clip(alpha - (1.0 / 255.0));
                return half4(max(c, 0.0), alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
