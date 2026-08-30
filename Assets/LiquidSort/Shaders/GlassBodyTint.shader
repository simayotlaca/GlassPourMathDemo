// Transparent interior-only glass film.
//
// The mesh and mask use the exact same baked VesselProfile.QuadRect as the liquid,
// but this pass sits one sorting order behind it.  Opaque liquid therefore keeps its
// authored colour while the empty chamber receives a quiet navy-violet body tint and
// a broad lavender reflection.  No pixels from the rim, stem, foot or handle enter
// the pass because they are absent from the interior mask.
Shader "LiquidSort/GlassBodyTint"
{
    Properties
    {
        _MaskTex ("Interior Mask", 2D) = "white" {}
        _TintColor ("Body Tint", Color) = (0.12,0.10,0.30,0.18)
        _ReflectionColor ("Soft Reflection", Color) = (0.78,0.73,1.0,1.0)
        _EdgeColor ("Inner Edge", Color) = (0.025,0.03,0.10,1.0)
        _ReflectionAlpha ("Reflection Alpha", Range(0,0.25)) = 0.075
        _CoreAlpha ("Reflection Core Alpha", Range(0,0.15)) = 0.028
        _EdgeAlpha ("Edge Alpha", Range(0,0.20)) = 0.05
        _ReflectionCenter ("Reflection Center", Range(0,1)) = 0.23
        _ReflectionWidth ("Reflection Width", Range(0.02,0.5)) = 0.18
        _CoreWidth ("Reflection Core Width", Range(0.01,0.2)) = 0.045
        _EdgeWidthPixels ("Edge Width Pixels", Range(1,24)) = 10
        _BottomCutoff ("Bowl Bottom", Range(0,1)) = 0.0
        _BottomFeather ("Bowl Bottom Feather", Range(0.001,0.1)) = 0.02
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
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
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _MaskTex;
            float4 _MaskTex_TexelSize;
            fixed4 _TintColor;
            fixed4 _ReflectionColor;
            fixed4 _EdgeColor;
            float _ReflectionAlpha;
            float _CoreAlpha;
            float _EdgeAlpha;
            float _ReflectionCenter;
            float _ReflectionWidth;
            float _CoreWidth;
            float _EdgeWidthPixels;
            float _BottomCutoff;
            float _BottomFeather;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float MaskAt(float2 uv)
            {
                fixed4 sampled = tex2D(_MaskTex, uv);
                return max(sampled.r, sampled.a);
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float mask = MaskAt(input.uv);
                float bowlGate = smoothstep(_BottomCutoff,
                    _BottomCutoff + max(_BottomFeather, 0.001), input.uv.y);
                float coverage = mask * bowlGate;
                clip(coverage - 0.001);

                // Erode the mask with four taps.  The difference is an interior edge
                // band that follows a cone just as accurately as it follows a cylinder.
                float2 reach = _MaskTex_TexelSize.xy * _EdgeWidthPixels;
                float eroded = min(min(MaskAt(input.uv + float2(reach.x, 0.0)),
                                       MaskAt(input.uv - float2(reach.x, 0.0))),
                                   min(MaskAt(input.uv + float2(0.0, reach.y)),
                                       MaskAt(input.uv - float2(0.0, reach.y))));
                float edge = saturate(mask - eroded);

                float broadX = (input.uv.x - _ReflectionCenter)
                               / max(_ReflectionWidth, 0.001);
                float coreX = (input.uv.x - (_ReflectionCenter - 0.035))
                              / max(_CoreWidth, 0.001);
                float broad = exp(-0.5 * broadX * broadX);
                float core = exp(-0.5 * coreX * coreX);
                float vertical = smoothstep(_BottomCutoff + 0.02,
                                            _BottomCutoff + 0.16, input.uv.y)
                               * (1.0 - smoothstep(0.88, 1.02, input.uv.y));
                broad *= vertical;
                core *= vertical;

                float reflectionMix = saturate(broad * 0.58 + core * 0.42);
                fixed3 rgb = lerp(_TintColor.rgb, _EdgeColor.rgb, edge * 0.42);
                rgb = lerp(rgb, _ReflectionColor.rgb, reflectionMix * 0.58);

                float alpha = _TintColor.a
                            + broad * _ReflectionAlpha
                            + core * _CoreAlpha
                            + edge * _EdgeAlpha;
                return fixed4(rgb, saturate(alpha) * coverage);
            }
            ENDCG
        }
    }
    Fallback Off
}
