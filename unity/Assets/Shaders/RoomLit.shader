// OlliVR room shader: baked per-vertex light (soft shadows, bounce, occlusion) refined per pixel.
//
// The mod draws with Graphics.DrawMeshNow outside any render pipeline, so nothing here relies on Unity's
// light setup: the lights come in as plain arrays in the room's own (model) space, set by the mod.
//
//  * Normal detail: the baked light is re-shaded with the normal map against the dominant incoming light
//    direction at that pixel, so grain, stone joints and weave catch the light from the actual lamps.
//  * Specular: GGX highlights from each light (lamp, fire, candles, sconces), shadowed by the baked
//    light level, with roughness from a roughness map / per-material value.
//  * Sheen: a faint fresnel rim lifted from the baked light, for soft materials.
//
// Vertex data: POSITION, NORMAL, TEXCOORD0 (uv), COLOR (baked light * tint, alpha = opacity).
Shader "OlliVR/RoomLit"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _BumpMap ("Normal (tangent space)", 2D) = "bump" {}
        _BumpScale ("Normal strength", Float) = 1
        _Roughness ("Roughness", Range(0.05, 1)) = 0.6
        _Specular ("Specular strength", Range(0, 2)) = 0.5
        _Sheen ("Sheen", Range(0, 1)) = 0
        _Exposure ("Exposure", Float) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }
        Pass
        {
            ZWrite Off                          // depth comes from the prepass
            ZTest LEqual
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha     // the floor is partly see-through to its reflection

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #pragma only_renderers d3d11
            #include "UnityCG.cginc"

            #define MAX_LIGHTS 16

            sampler2D _MainTex;
            sampler2D _BumpMap;
            float _BumpScale, _Roughness, _Specular, _Sheen, _Exposure;

            // Room-local space. xyz = position, w = falloff radius.
            float4 _OlliLightPos[MAX_LIGHTS];
            // rgb = colour (linear-ish, as baked), w = kind: 0 point, 1 lamp (fabric shade), 2 fire.
            float4 _OlliLightCol[MAX_LIGHTS];
            int _OlliLightCount;
            float4 _OlliEye;          // eye position in room-local space
            float _OlliFire;          // live fire intensity multiplier

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 lp : TEXCOORD1;     // room-local position
                float3 ln : TEXCOORD2;     // room-local normal
                float4 col : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.lp = v.vertex.xyz;
                o.ln = v.normal;
                o.col = v.color;
                return o;
            }

            // Tangent frame from screen-space derivatives (no mesh tangents needed).
            float3x3 CotangentFrame(float3 n, float3 p, float2 uv)
            {
                float3 dp1 = ddx(p), dp2 = ddy(p);
                float2 duv1 = ddx(uv), duv2 = ddy(uv);
                float3 dp2perp = cross(dp2, n);
                float3 dp1perp = cross(n, dp1);
                float3 t = dp2perp * duv1.x + dp1perp * duv2.x;
                float3 b = dp2perp * duv1.y + dp1perp * duv2.y;
                float invmax = rsqrt(max(max(dot(t, t), dot(b, b)), 1e-12));
                return float3x3(t * invmax, b * invmax, n);
            }

            float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

            float D_GGX(float nh, float a)
            {
                float a2 = a * a;
                float d = nh * nh * (a2 - 1) + 1;
                return a2 / (UNITY_PI * d * d + 1e-6);
            }

            float V_SmithJointApprox(float nl, float nv, float a)
            {
                float gv = nl * (nv * (1 - a) + a);
                float gl = nv * (nl * (1 - a) + a);
                return 0.5 / (gv + gl + 1e-5);
            }

            float4 frag(v2f i, bool front : SV_IsFrontFace) : SV_Target
            {
                float4 albedo = tex2D(_MainTex, i.uv);
                float3 baked = i.col.rgb;

                float3 ng = normalize(i.ln);
                float3 v = normalize(_OlliEye.xyz - i.lp);
                if (dot(ng, v) < 0) ng = -ng;                 // two-sided thin geometry

                float3 tn = UnpackNormal(tex2D(_BumpMap, i.uv));
                tn.xy *= _BumpScale;
                float3x3 tbn = CotangentFrame(ng, i.lp, i.uv);
                float3 n = normalize(mul(normalize(tn), tbn));

                // Gather lights: dominant direction for re-shading, and GGX highlights.
                float a = _Roughness * _Roughness;
                float nv = saturate(dot(n, v)) + 1e-4;
                float3 dom = 0;
                float3 spec = 0;
                [loop] for (int k = 0; k < _OlliLightCount; k++)
                {
                    float3 lpos = _OlliLightPos[k].xyz;
                    float radius = _OlliLightPos[k].w;
                    float3 col = _OlliLightCol[k].rgb;
                    float kind = _OlliLightCol[k].w;
                    float3 d = lpos - i.lp;
                    float dist = length(d);
                    float3 l = d / max(dist, 1e-3);
                    float att = 1.0 / (1.0 + (dist / radius) * (dist / radius));
                    if (kind > 1.5) col *= _OlliFire;
                    else if (kind > 0.5) { float vy = abs(l.y); att *= 0.4 + 1.1 * vy * vy; }
                    if (att * Luma(col) < 0.002) continue;      // too faint to matter here
                    float nlg = dot(ng, l);
                    if (nlg <= 0) continue;                    // the baked light has this surface facing away
                    float w = Luma(col) * att;
                    dom += l * w;
                    float nl = saturate(dot(n, l));
                    float3 h = normalize(l + v);
                    float nh = saturate(dot(n, h));
                    float lh = saturate(dot(l, h));
                    float f = 0.04 + 0.96 * pow(1 - lh, 5);
                    spec += col * att * nl * D_GGX(nh, a) * V_SmithJointApprox(nl, nv, a) * f;
                }

                // Re-shade the baked light with the detail normal (ratio of detail to geometric lambert).
                float dl = length(dom);
                float detail = 1;
                if (dl > 1e-5)
                {
                    float3 ld = dom / dl;
                    float wrap = 0.1;
                    detail = (saturate(dot(n, ld)) + wrap) / (saturate(dot(ng, ld)) + wrap);
                    detail = clamp(detail, 0.3, 2.0);
                }

                // Highlights only where the baked light says the surface is lit (shadows from the baker).
                float lit = smoothstep(0.015, 0.22, Luma(baked));
                float3 c = albedo.rgb * baked * detail;
                c += spec * _Specular * lit * UNITY_PI;
                // Soft fresnel sheen for fabric, picking up the surrounding light.
                float rim = pow(1 - saturate(dot(ng, v)), 4);
                c += baked * albedo.rgb * rim * _Sheen;

                c *= _Exposure;
                // Gentle shoulder so strong highlights don't clip harshly.
                c = c / (1 + max(0, Luma(c) - 0.8) * 0.6);
                return float4(c, i.col.a * albedo.a);
            }
            ENDCG
        }
    }
}
