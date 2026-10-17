Shader "OsrsPort/VertexColorLit"
{
    Properties
    {
        _Ambient ("Ambient", Range(0, 1)) = 0.45
        _HasTex ("Has Tex", Float) = 0
        [NoScaleOffset] _TexArr ("Textures", 2DArray) = "" {}
        [NoScaleOffset] _ScrollTex ("Scroll", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Ambient;
                half _HasTex;
            CBUFFER_END

            TEXTURE2D_ARRAY(_TexArr);
            SAMPLER(sampler_TexArr);
            TEXTURE2D(_ScrollTex);
            SAMPLER(sampler_ScrollTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                float3 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                half fogFactor : TEXCOORD1;
                float3 uv : TEXCOORD2;
                half4 color : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                o.uv = input.uv;
                o.color = input.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 light = i.color.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                light = SRGBToLinear(light);
                #endif
                half3 c = light;
                if (_HasTex > 0.5 && i.uv.z > 0.5)
                {
                    float slice = i.uv.z - 1.0;
                    float2 scroll = SAMPLE_TEXTURE2D_LOD(_ScrollTex, sampler_ScrollTex, float2((slice + 0.5) / 256.0, 0.5), 0).rg;
                    half4 tex = SAMPLE_TEXTURE2D_ARRAY(_TexArr, sampler_TexArr, i.uv.xy + scroll * _Time.y, slice);
                    clip(tex.a - 0.5);
                    c = tex.rgb * light;
                }
                c = MixFog(c, i.fogFactor);
                return half4(c, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Ambient;
                half _HasTex;
            CBUFFER_END
            TEXTURE2D_ARRAY(_TexArr);
            SAMPLER(sampler_TexArr);
            TEXTURE2D(_ScrollTex);
            SAMPLER(sampler_ScrollTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            half frag(Varyings i) : SV_Target
            {
                if (_HasTex > 0.5 && i.uv.z > 0.5)
                {
                    float slice = i.uv.z - 1.0;
                    float2 scroll = SAMPLE_TEXTURE2D_LOD(_ScrollTex, sampler_ScrollTex, float2((slice + 0.5) / 256.0, 0.5), 0).rg;
                    clip(SAMPLE_TEXTURE2D_ARRAY(_TexArr, sampler_TexArr, i.uv.xy + scroll * _Time.y, slice).a - 0.5);
                }
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Ambient;
                half _HasTex;
            CBUFFER_END
            TEXTURE2D_ARRAY(_TexArr);
            SAMPLER(sampler_TexArr);
            TEXTURE2D(_ScrollTex);
            SAMPLER(sampler_ScrollTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float3 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 uv : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = input.uv;
                return o;
            }

            half4 frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                if (_HasTex > 0.5 && i.uv.z > 0.5)
                {
                    float slice = i.uv.z - 1.0;
                    float2 scroll = SAMPLE_TEXTURE2D_LOD(_ScrollTex, sampler_ScrollTex, float2((slice + 0.5) / 256.0, 0.5), 0).rg;
                    clip(SAMPLE_TEXTURE2D_ARRAY(_TexArr, sampler_TexArr, i.uv.xy + scroll * _Time.y, slice).a - 0.5);
                }
                float3 n = normalize(i.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);
                return half4(n, 0.0);
            }
            ENDHLSL
        }
    }
}
