// Sprites/Default-compatible pass-through for the authored Royal glass plates.
//
// These sprites already contain their cyan rims and reflections. This shader leaves
// that artwork intact and adds only the small amount of scene overhead light that can
// land on opaque painted glass pixels. Transparent cavities remain transparent, so the
// light never becomes a rectangular wash over the liquid or the stage.
Shader "LiquidSort/RoyalGlassSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _OverheadColor ("Scene Overhead Light", Color) = (1.0,0.97,0.91,1)
        _OverheadStrength ("Scene Overhead Strength", Range(0,0.30)) = 0.12
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
            #pragma fragment RoyalGlassFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            fixed4 _OverheadColor;
            float _OverheadStrength;

            fixed4 RoyalGlassFrag(v2f input) : SV_Target
            {
                fixed4 color = SampleSpriteTexture(input.texcoord) * input.color;
                float luminance = dot(color.rgb, float3(0.299, 0.587, 0.114));

                // Broad source above the stage. Preserve the hand-painted lower base and
                // let authored bright rim/reflection pixels catch more of the light than
                // the navy casing. Headroom lighting avoids bleaching the cyan contour.
                float top = smoothstep(0.56, 0.94, input.texcoord.y);
                float authoredResponse = lerp(0.25, 1.0,
                    smoothstep(0.28, 0.82, luminance));
                float amount = saturate(_OverheadStrength * top * authoredResponse);
                color.rgb += (1.0 - saturate(color.rgb))
                           * _OverheadColor.rgb * amount;

                // Match Sprites/Default's premultiplied output exactly.
                color.rgb *= color.a;
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
