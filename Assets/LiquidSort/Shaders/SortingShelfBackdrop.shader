// Quiet portrait backdrop for the hand-authored sorting shelf scene.
// The shelves and posts are real scene sprites; this shader only supplies depth,
// a restrained central glow and edge falloff behind them.
Shader "LiquidSort/SortingShelfBackdrop"
{
    Properties
    {
        _TopColor ("Top", Color) = (0.055, 0.020, 0.105, 1)
        _BottomColor ("Bottom", Color) = (0.012, 0.008, 0.035, 1)
        _GlowColor ("Centre Glow", Color) = (0.255, 0.055, 0.390, 1)
        _GlowStrength ("Glow Strength", Range(0, 1)) = 0.42
        _Vignette ("Vignette", Range(0, 1)) = 0.58
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            fixed4 _TopColor;
            fixed4 _BottomColor;
            fixed4 _GlowColor;
            float _GlowStrength;
            float _Vignette;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.screenPos = ComputeScreenPos(output.vertex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float2 uv = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                fixed3 color = lerp(_BottomColor.rgb, _TopColor.rgb, saturate(uv.y));

                float2 glowPoint = (uv - float2(0.50, 0.58)) * float2(1.12, 0.78);
                float glow = saturate(1.0 - length(glowPoint) / 0.63);
                glow = glow * glow * (3.0 - 2.0 * glow);
                color = lerp(color, _GlowColor.rgb, glow * _GlowStrength);

                float2 edgePoint = (uv - 0.5) * float2(1.12, 0.92);
                float edge = smoothstep(0.27, 0.67, length(edgePoint));
                color *= lerp(1.0, 1.0 - _Vignette, edge);

                return fixed4(color, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
