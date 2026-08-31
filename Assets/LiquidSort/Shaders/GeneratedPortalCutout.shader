Shader "LiquidSort/GeneratedPortalCutout"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ChromaLow ("Transparent Chroma", Range(0, 0.25)) = 0.025
        _ChromaHigh ("Opaque Chroma", Range(0, 0.25)) = 0.085
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
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
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment PortalFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON

            #include "UnitySprites.cginc"

            float _ChromaLow;
            float _ChromaHigh;

            fixed4 PortalFrag(v2f IN) : SV_Target
            {
                fixed4 sample = SampleSpriteTexture(IN.texcoord) * IN.color;
                fixed maximum = max(sample.r, max(sample.g, sample.b));
                fixed minimum = min(sample.r, min(sample.g, sample.b));
                fixed chroma = maximum - minimum;

                // Image generation supplied an opaque neutral checkerboard instead of
                // alpha.  The portal itself is strongly gold/purple, so chroma is a
                // deterministic mobile-safe matte for both the outside and the doorway.
                fixed matte = smoothstep(_ChromaLow, _ChromaHigh, chroma);
                sample.a *= matte;
                return sample;
            }
            ENDCG
        }
    }
}
