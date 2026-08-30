// Shared palette remap for the supplied glass sprites.
//
// The PNGs already contain the artist's modelling, antialiasing and highlight
// placement. This shader leaves their alpha and luminance structure untouched and
// only translates the baked saturated blue ramp into one common glass palette.
Shader "LiquidSort/AuthoredGlassPalette"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ShadowColor ("Glass Shadow", Color) = (0.310,0.365,0.525,1)
        _MidColor ("Glass Mid", Color) = (0.557,0.573,0.706,1)
        _HighlightColor ("Glass Highlight", Color) = (0.851,0.941,0.933,1)
        _ShadowPoint ("Source Shadow Point", Range(0,1)) = 0.33
        _MidPoint ("Source Mid Point", Range(0,1)) = 0.50
        _HighlightPoint ("Source Highlight Point", Range(0,1)) = 0.68
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
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
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment PaletteFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            fixed4 _ShadowColor;
            fixed4 _MidColor;
            fixed4 _HighlightColor;
            float _ShadowPoint;
            float _MidPoint;
            float _HighlightPoint;

            fixed4 PaletteFrag(v2f input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord);
                float luminance = dot(source.rgb, float3(0.299, 0.587, 0.114));

                float shadowToMid = smoothstep(_ShadowPoint,
                    max(_MidPoint, _ShadowPoint + 1e-4), luminance);
                float midToHighlight = smoothstep(_MidPoint,
                    max(_HighlightPoint, _MidPoint + 1e-4), luminance);

                fixed3 mapped = lerp(_ShadowColor.rgb, _MidColor.rgb, shadowToMid);
                mapped = lerp(mapped, _HighlightColor.rgb, midToHighlight);

                fixed4 output = fixed4(mapped, source.a) * input.color;
                // Sprites use premultiplied output with Blend One OneMinusSrcAlpha.
                // Multiplying here keeps antialiased edges clean on the lavender field.
                output.rgb *= output.a;
                return output;
            }
            ENDCG
        }
    }

    Fallback Off
}
