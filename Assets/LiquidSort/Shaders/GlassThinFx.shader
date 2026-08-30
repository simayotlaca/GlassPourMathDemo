// A deliberately narrow glass pass for authored vessel sprites.
//
// The sprite's own alpha is the first mask, so this shader can only brighten pixels
// the artist already painted as glass. A second local-space mask keeps that light on
// the side walls and a thin band at the interior floor. The liquid centre is therefore
// untouched, unlike the old full-silhouette GlassLight texture.
Shader "LiquidSort/GlassThinFX"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _FxColor ("Key Glass Light", Color) = (0.82,0.94,1,1)
        _FxColor2 ("Fill Glass Light", Color) = (0.36,0.66,1,1)
        _SideStrength ("Side Strength", Range(0,1)) = 0.42
        _BottomStrength ("Bottom Lens Strength", Range(0,1)) = 0.0
        _SideStart ("Side Start", Range(0,1)) = 0.28
        _SideFull ("Side Full", Range(0,1)) = 0.78
        _BottomBelow ("Floor Seam Search Margin Below", Range(0.001,0.25)) = 0.015
        _BottomHeight ("Floor Seam Search Margin Above", Range(0.001,0.35)) = 0.015
        [HideInInspector] _InteriorRect ("Interior Rect", Vector) = (-0.5,-0.5,0.5,0.5)
        [HideInInspector] _VisibleFloorY ("Optical Liquid Floor Y", Float) = -10000
        [HideInInspector] _VisibleBottomY ("Visible Liquid Bottom Y", Float) = -10000
        [HideInInspector] _MaskTex ("Interior Shape", 2D) = "black" {}
        [HideInInspector] _MaskRect ("Mask Local Rect", Vector) = (-0.5,-0.5,1,1)
        [HideInInspector] _MaskReach ("Mask Probe UV Reach", Vector) = (0.03,0.03,0,0)
        [HideInInspector] _UseMask ("Use Interior Shape", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
            // The fragment mask uses object-space local position. Combining several
            // vessels into one dynamic batch would replace that coordinate system.
            "DisableBatching" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnityCG.cginc"
            #include "UnitySprites.cginc"

            struct appdata_fx
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f_fx
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float2 localPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _FxColor;
            fixed4 _FxColor2;
            float _SideStrength;
            float _BottomStrength;
            float _SideStart;
            float _SideFull;
            float _BottomBelow;
            float _BottomHeight;
            float4 _InteriorRect;
            float _VisibleFloorY;
            float _VisibleBottomY;
            float4 _MainTex_TexelSize;
            sampler2D _MaskTex;
            float4 _MaskRect;
            float4 _MaskReach;
            float _UseMask;

            v2f_fx vert(appdata_fx input)
            {
                v2f_fx output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float4 local = UnityFlipSprite(input.vertex, _Flip);
                output.vertex = UnityObjectToClipPos(local);
                output.texcoord = input.texcoord;
                output.localPos = local.xy;
                output.color = input.color * _Color * _RendererColor;
                #ifdef PIXELSNAP_ON
                output.vertex = UnityPixelSnap(output.vertex);
                #endif
                return output;
            }

            fixed4 frag(v2f_fx input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord);
                fixed sourceAlpha = source.a;
                float2 size = max(_InteriorRect.zw - _InteriorRect.xy, float2(1e-4, 1e-4));
                float2 p = (input.localPos - _InteriorRect.xy) / size;

                // The body gate omits the broad upper rim. Profiled vessels then read
                // their baked interior mask at a wall-sized reach. The max/min ring says
                // whether this authored pixel actually touches the cavity; a Sobel-like
                // gradient says whether that boundary is a side wall or the floor. This
                // follows a tapered cocktail bowl and ignores a mug handle automatically.
                float sideDistance = abs(p.x * 2.0 - 1.0);
                // Never let the vertical side reflection reach the opaque base. That
                // produced the square columns visible at the left and right ends of the
                // mug foot. Start above the measured visible bottom and feather upward;
                // the dedicated floor-edge term below handles the actual junction.
                float sideStartY = _VisibleBottomY + size.x * 0.018;
                float sideFullY = _VisibleBottomY + size.x * 0.075;
                float bodyGate = smoothstep(sideStartY, sideFullY, input.localPos.y)
                               * (1.0 - smoothstep(0.80, 0.98, p.y));
                // Search only between the optical floor and the first fully visible
                // liquid row. The actual curved seam is selected below from the artist
                // sprite's alpha edge, so this range never becomes a horizontal band.
                float below = max(_BottomBelow * size.x, 1e-3);
                float above = max(_BottomHeight * size.x, 1e-3);
                float searchMin = min(_VisibleFloorY, _VisibleBottomY) - below;
                float searchMax = max(_VisibleFloorY, _VisibleBottomY) + above;
                float lowerFeather = max(below * 0.35, 1e-3);
                float upperFeather = max(above * 0.35, 1e-3);
                float bottomWindow = smoothstep(searchMin, searchMin + lowerFeather,
                                                input.localPos.y)
                                   * (1.0 - smoothstep(searchMax - upperFeather, searchMax,
                                                      input.localPos.y));

                // Detect the authored transition from transparent cavity above to
                // navy/translucent glass below. This follows a curved bowl or mug base
                // pixel-for-pixel and cannot spill into the transparent liquid centre.
                // Four source texels survive bilinear filtering and the downscale to
                // gameplay size, while still selecting an edge rather than a strip.
                float edgeStep = max(_MainTex_TexelSize.y * 4.0, 1e-5);
                float alphaAbove = SampleSpriteTexture(
                    input.texcoord + float2(0.0, edgeStep)).a;
                float alphaBelow = SampleSpriteTexture(
                    input.texcoord - float2(0.0, edgeStep)).a;
                float enteringGlass = saturate(alphaBelow - alphaAbove);
                float floorEdge = smoothstep(0.04, 0.30, enteringGlass);
                float sourceLum = dot(source.rgb, float3(0.299, 0.587, 0.114));
                float authoredDark = 1.0 - smoothstep(0.34, 0.72, sourceLum);
                floorEdge *= bottomWindow * lerp(0.35, 1.0, authoredDark);

                float leftSide;
                float rightSide;
                float bottom;

                UNITY_BRANCH
                if (_UseMask > 0.5)
                {
                    float2 uv = (input.localPos - _MaskRect.xy)
                              / max(_MaskRect.zw, float2(1e-4, 1e-4));
                    float2 r = max(_MaskReach.xy, float2(1e-4, 1e-4));
                    float mL = tex2D(_MaskTex, uv - float2(r.x, 0)).a;
                    float mR = tex2D(_MaskTex, uv + float2(r.x, 0)).a;
                    float mD = tex2D(_MaskTex, uv - float2(0, r.y)).a;
                    float mU = tex2D(_MaskTex, uv + float2(0, r.y)).a;
                    float mLD = tex2D(_MaskTex, uv - r).a;
                    float mRU = tex2D(_MaskTex, uv + r).a;
                    float mLU = tex2D(_MaskTex, uv + float2(-r.x, r.y)).a;
                    float mRD = tex2D(_MaskTex, uv + float2(r.x, -r.y)).a;

                    float minMask = min(min(mL, mR), min(min(mD, mU),
                        min(min(mLD, mRU), min(mLU, mRD))));
                    float maxMask = max(max(mL, mR), max(max(mD, mU),
                        max(max(mLD, mRU), max(mLU, mRD))));
                    float boundary = saturate(maxMask - minMask);
                    float2 into = float2(
                        (mRU + 2.0 * mR + mRD) - (mLU + 2.0 * mL + mLD),
                        (mLU + 2.0 * mU + mRU) - (mLD + 2.0 * mD + mRD));
                    float2 outward = -normalize(into + float2(1e-5, 1e-5));
                    float uvGate = smoothstep(-0.02, 0.01, uv.x)
                                 * (1.0 - smoothstep(0.99, 1.02, uv.x))
                                 * smoothstep(-0.02, 0.01, uv.y)
                                 * (1.0 - smoothstep(0.99, 1.02, uv.y));
                    boundary *= uvGate;

                    float side = boundary
                               * smoothstep(0.48, 0.90, abs(outward.x)) * bodyGate;
                    leftSide = side * saturate(-outward.x);
                    rightSide = side * saturate(outward.x) * 0.72;
                    // Centre-mask coverage rejects the outer silhouette. Source alpha
                    // remains the final authority in the output below, so only authored
                    // seam pixels receive this separate glass light.
                    float inside = tex2D(_MaskTex, uv).a;
                    float across = lerp(0.45, 1.0,
                        1.0 - smoothstep(0.52, 0.96, sideDistance));
                    bottom = floorEdge * smoothstep(0.08, 0.72, inside)
                           * across * uvGate;
                }
                else
                {
                    // Loose/unbaked bottles have no persistent mask asset. Retain the
                    // safe analytic fallback, restricted to the interior rect so stems
                    // and handles stay dark.
                    float rectGate = smoothstep(-0.075, -0.005, p.x)
                                   * (1.0 - smoothstep(1.005, 1.075, p.x));
                    float side = smoothstep(_SideStart,
                        max(_SideFull, _SideStart + 1e-3), sideDistance)
                        * bodyGate * rectGate;
                    leftSide = side * saturate(1.0 - p.x);
                    rightSide = side * saturate(p.x) * 0.72;
                    float lensAcross = 1.0 - smoothstep(0.52, 0.96, sideDistance);
                    bottom = floorEdge * lerp(0.32, 1.0, lensAcross) * rectGate;
                }

                // Side light follows the renderer alpha so selection can pulse it. The
                // floor seam is a separate, stable glass correction: scaling it by the
                // resting 0.24 side intensity made the navy ridge remain nearly black.
                float sideAmount = sourceAlpha * input.color.a;
                float3 sideLight = (_FxColor.rgb * leftSide * _SideStrength * _FxColor.a
                                  + _FxColor2.rgb * rightSide * _SideStrength * _FxColor2.a)
                                 * sideAmount;
                float3 floorLight = _FxColor.rgb * bottom * _BottomStrength
                                  * _FxColor.a * sourceAlpha;
                return fixed4(sideLight + floorLight, 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
