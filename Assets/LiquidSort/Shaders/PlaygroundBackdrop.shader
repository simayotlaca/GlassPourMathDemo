// Screen-space showcase background for the all-glasses playground.
// One procedural quad keeps the backdrop independent from vessel transforms and
// avoids temporary editor-only sprites that would disappear after reopening the scene.
Shader "LiquidSort/PlaygroundBackdrop"
{
    Properties
    {
        _WorldHeight ("Visible World Height", Float) = 10.1
        _TopColor ("Backdrop Top", Color) = (0.035,0.082,0.169,1)
        _BottomColor ("Backdrop Bottom", Color) = (0.145,0.047,0.149,1)
        _BayTopColor ("Bay Top", Color) = (0.067,0.114,0.220,1)
        _BayBottomColor ("Bay Bottom", Color) = (0.090,0.067,0.149,1)
        _PillarColor ("Burgundy Pillars", Color) = (0.259,0.063,0.161,1)
        _BevelColor ("Pillar Bevel", Color) = (0.545,0.180,0.282,1)
        _CanopyColor ("Top Canopy", Color) = (0.188,0.055,0.161,1)
        _AlcoveUpper ("Upper Alcove", Color) = (0.043,0.090,0.192,1)
        _AlcoveLower ("Lower Alcove", Color) = (0.082,0.067,0.161,1)
        _ArchColor ("Arch Rim", Color) = (0.545,0.278,0.259,1)
        _CeilingColor ("Ceiling Accent", Color) = (0.392,0.867,0.886,1)
        _ShelfShadow ("Shelf Shadow", Color) = (0.145,0.035,0.114,1)
        _ShelfBody ("Shelf Body", Color) = (0.455,0.090,0.184,1)
        _ShelfLip ("Shelf Lip", Color) = (0.737,0.196,0.286,1)
        _ShelfHighlight ("Shelf Highlight", Color) = (0.910,0.380,0.424,1)
        _ShelfUnderlight ("Shelf Underlight", Color) = (0.055,0.659,0.776,1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent-100"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
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
            #pragma target 3.0
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

            float _WorldHeight;
            fixed4 _TopColor;
            fixed4 _BottomColor;
            fixed4 _BayTopColor;
            fixed4 _BayBottomColor;
            fixed4 _PillarColor;
            fixed4 _BevelColor;
            fixed4 _CanopyColor;
            fixed4 _AlcoveUpper;
            fixed4 _AlcoveLower;
            fixed4 _ArchColor;
            fixed4 _CeilingColor;
            fixed4 _ShelfShadow;
            fixed4 _ShelfBody;
            fixed4 _ShelfLip;
            fixed4 _ShelfHighlight;
            fixed4 _ShelfUnderlight;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.screenPos = ComputeScreenPos(output.vertex);
                return output;
            }

            float RoundedBox(float2 p, float2 halfSize, float radius)
            {
                radius = min(radius, min(halfSize.x, halfSize.y));
                float2 q = abs(p) - halfSize + radius;
                float distance = length(max(q, 0.0))
                               + min(max(q.x, q.y), 0.0) - radius;
                float antialias = max(fwidth(distance), 0.0015);
                return 1.0 - smoothstep(-antialias, antialias, distance);
            }

            void DrawShelf(inout fixed3 color, float2 world, float topY)
            {
                float shadow = RoundedBox(world - float2(0.0, topY - 0.315),
                    float2(2.67, 0.29), 0.22);
                float underlight = RoundedBox(world - float2(0.0, topY - 0.49),
                    float2(2.63, 0.035), 0.032);
                float fascia = RoundedBox(world - float2(0.0, topY - 0.265),
                    float2(2.59, 0.18), 0.17);
                float lip = RoundedBox(world - float2(0.0, topY - 0.065),
                    float2(2.65, 0.07), 0.065);
                float highlight = RoundedBox(world - float2(0.0, topY),
                    float2(2.55, 0.018), 0.016);

                color = lerp(color, _ShelfShadow.rgb, shadow * 0.96);
                color = lerp(color, _ShelfUnderlight.rgb, underlight * 0.78);

                float fasciaShade = saturate((world.y - (topY - 0.445)) / 0.36);
                fixed3 body = _ShelfBody.rgb * lerp(0.66, 1.0, fasciaShade);
                color = lerp(color, body, fascia);
                color = lerp(color, _ShelfLip.rgb, lip);
                color = lerp(color, _ShelfHighlight.rgb, highlight * 0.82);
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float2 uv = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                float aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float2 world = float2((uv.x - 0.5) * _WorldHeight * aspect,
                                      (uv.y - 0.5) * _WorldHeight);

                float vertical = saturate(uv.y);
                fixed3 color = lerp(_BottomColor.rgb, _TopColor.rgb, vertical);

                // A dark central showcase gives the translucent glass a stable local
                // contrast while the burgundy frame keeps the reference's warm mood.
                float bay = RoundedBox(world - float2(0.0, -0.05),
                    float2(2.54, 4.86), 0.34);
                fixed3 bayColor = lerp(_BayBottomColor.rgb, _BayTopColor.rgb, vertical);
                color = lerp(color, bayColor, bay);

                float upperAlcove = RoundedBox(world - float2(0.0, 2.50),
                    float2(2.38, 1.66), 0.28);
                float lowerAlcove = RoundedBox(world - float2(0.0, -1.75),
                    float2(2.38, 1.86), 0.28);
                color = lerp(color, _AlcoveUpper.rgb, upperAlcove * 0.88);
                color = lerp(color, _AlcoveLower.rgb, lowerAlcove * 0.88);

                float leftPillar = RoundedBox(world - float2(-2.58, 0.0),
                    float2(0.26, 5.10), 0.20);
                float rightPillar = RoundedBox(world - float2(2.58, 0.0),
                    float2(0.26, 5.10), 0.20);
                color = lerp(color, _PillarColor.rgb,
                    saturate(leftPillar + rightPillar));

                float leftBevel = RoundedBox(world - float2(-2.31, 0.0),
                    float2(0.038, 4.88), 0.030);
                float rightBevel = RoundedBox(world - float2(2.31, 0.0),
                    float2(0.038, 4.88), 0.030);
                color = lerp(color, _BevelColor.rgb,
                    saturate(leftBevel + rightBevel) * 0.65);

                float canopy = RoundedBox(world - float2(0.0, 4.48),
                    float2(2.55, 0.39), 0.22);
                color = lerp(color, _CanopyColor.rgb, canopy);

                // A restrained arch and ceiling accents echo the reference without
                // becoming brighter than the glass highlights.
                float2 archPoint = float2(world.x / 2.18, (world.y - 2.13) / 2.06);
                float archDistance = abs(length(archPoint) - 1.0);
                float arch = (1.0 - smoothstep(0.020, 0.055, archDistance))
                           * smoothstep(0.76, 1.05, world.y);
                color = lerp(color, _ArchColor.rgb, arch * 0.40);

                float ceiling1 = RoundedBox(world - float2(0.0, 4.53),
                    float2(1.25, 0.028), 0.025);
                float ceiling2 = RoundedBox(world - float2(0.0, 4.27),
                    float2(0.95, 0.026), 0.023);
                float ceiling3 = RoundedBox(world - float2(0.0, 4.04),
                    float2(0.68, 0.024), 0.021);
                float ceiling = ceiling1 * 0.12 + ceiling2 * 0.09 + ceiling3 * 0.06;
                color = lerp(color, _CeilingColor.rgb, saturate(ceiling));

                DrawShelf(color, world, 0.695);
                DrawShelf(color, world, -3.397);

                // The untouched cocktail art has a slightly higher foot than the shot.
                // A small integrated plinth seats it without moving either vessel.
                float plinth = RoundedBox(world - float2(1.20, 0.84),
                    float2(0.61, 0.14), 0.12);
                float plinthTop = RoundedBox(world - float2(1.20, 0.98),
                    float2(0.58, 0.022), 0.020);
                color = lerp(color, _ShelfBody.rgb * 1.08, plinth);
                color = lerp(color, _ShelfHighlight.rgb, plinthTop * 0.74);

                // Soft vignette contains the composition and suppresses bright corners.
                float2 vignettePoint = float2((uv.x - 0.5) * 1.15,
                                              (uv.y - 0.50) * 0.84);
                float vignette = saturate(dot(vignettePoint, vignettePoint) * 1.35);
                color *= lerp(1.0, 0.62, vignette);

                return fixed4(color, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
