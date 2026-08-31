// Additive light pass for one bottle.
//
// Legacy sprites already contain encoded RGB + intensity alpha and pass straight
// through SpriteFrag. Baked vessel profiles instead bind their existing interior
// mask as _MainTex; the fragment shader evaluates the three glass highlights on the
// GPU. That path performs no runtime pixel readback, distance field, texture upload
// or per-vessel material clone.
Shader "LiquidSort/GlassLight"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
        [HideInInspector] _UseProfileMask ("Use Profile Mask", Float) = 0
        [HideInInspector] _ProfileMaskRect ("Profile Mask Rect", Vector) = (0,0,1,1)
        [HideInInspector] _ProfileLightRect ("Profile Light Rect", Vector) = (0,0,1,1)
        [HideInInspector] _PrimaryGloss ("Primary Gloss", Vector) = (-0.46,0.26,0.55,0.42)
        [HideInInspector] _SecondaryGloss ("Secondary Gloss", Vector) = (0.54,0.12,0.42,0)
        [HideInInspector] _ShoulderGloss ("Shoulder Gloss", Vector) = (0.84,0,0,0)
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
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment GlassLightFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            float _UseProfileMask;
            float4 _ProfileMaskRect;
            float4 _ProfileLightRect;
            float4 _PrimaryGloss;
            float4 _SecondaryGloss;
            float4 _ShoulderGloss;

            inline float GlassGauss(float value, float sigma)
            {
                float q = value / max(sigma, 1e-4);
                return exp(-q * q);
            }

            fixed4 GlassLightFrag(v2f IN) : SV_Target
            {
                // The default stays byte-for-byte compatible with the old shader. This
                // also protects MechanicRevealPresenter, which shares the material but
                // deliberately clears its MaterialPropertyBlock.
                if (_UseProfileMask < 0.5)
                    return SpriteFrag(IN);

                // The baked mask stores coverage, not a signed-distance channel. It is
                // therefore the exact cavity gate but cannot reproduce the old CPU pass's
                // broad inner-wall fade. Campaign rim/fill are deliberately zero; exact
                // future edge parity would require baking distance into another channel.
                float coverage = SampleSpriteTexture(IN.texcoord).a;
                float2 localPosition = _ProfileMaskRect.xy
                                     + IN.texcoord * _ProfileMaskRect.zw;
                float2 lightUv = (localPosition - _ProfileLightRect.xy)
                               / max(_ProfileLightRect.zw, float2(1e-4, 1e-4));
                float u = lightUv.x * 2.0 - 1.0;
                float v = lightUv.y;
                float lengthwise = smoothstep(0.02, 0.20, v)
                                 * (1.0 - smoothstep(0.70, 0.99, v));

                float primary = GlassGauss(u - _PrimaryGloss.x,
                    _PrimaryGloss.y) * _PrimaryGloss.z;
                float streakX = _PrimaryGloss.x + _PrimaryGloss.y * 0.55;
                float streak = GlassGauss(u - streakX,
                    _PrimaryGloss.y * 0.14) * _PrimaryGloss.w;
                float secondary = GlassGauss(u - _SecondaryGloss.x,
                    max(0.02, _SecondaryGloss.y)) * _SecondaryGloss.z;
                float shoulder = GlassGauss(u - _PrimaryGloss.x * 0.75, 0.20)
                               * GlassGauss(v - _ShoulderGloss.x, 0.055)
                               * _SecondaryGloss.w;

                const float3 sky = float3(0.62, 0.78, 1.0);
                const float3 warm = float3(1.0, 0.97, 0.92);
                float3 lit = coverage
                           * (sky * ((primary + secondary) * lengthwise)
                              + warm * (streak * lengthwise + shoulder));
                float peak = max(lit.r, max(lit.g, lit.b));
                float active = step(1.0 / 255.0, peak);
                lit *= active;
                peak *= active;

                // Recreate the old CPU texture encoding and SpriteFrag premultiply.
                // Renderer alpha (including selection highlight) is applied exactly once.
                float encodedAlpha = saturate(peak) * IN.color.a;
                float3 encodedRgb = saturate(lit / max(peak, 1e-6));
                return fixed4(encodedRgb * IN.color.rgb * encodedAlpha, encodedAlpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
