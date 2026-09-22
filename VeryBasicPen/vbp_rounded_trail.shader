Shader "SuperNekoya/VBP_Rounded_Trail"
{
	Properties
	{
		_Color ("Solid Color", Color) = (1,1,1,1)
		_Invisible ("Invisible Length", Float) = 10.0
		_Width ("Width", Float) = 0.03
	}
	SubShader
	{
		Tags { "RenderType"="Transparent" "Queue"="Transparent" }
		LOD 100
		Cull Off
		Blend SrcAlpha OneMinusSrcAlpha
		ZWrite Off

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma geometry geom
			#pragma fragment frag
			#pragma multi_compile_fog
			
			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
                fixed4 color : COLOR;

                UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct v2g
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
                fixed4 color : COLOR;

                UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct g2f
			{
				float4 vertex : SV_POSITION;
				float2 uv : TEXCOORD0;
				float d : TEXCOORD1;
                fixed4 color : COLOR;

                UNITY_VERTEX_OUTPUT_STEREO
			};

			float4 _Color;
			float _Width;
			float _Invisible;
			
			v2g vert (appdata v)
			{
				v2g o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

				o.vertex = v.vertex;
				o.uv = v.uv;
                o.color = v.color;
				return o;
			}

			[maxvertexcount(10)]
			void geom(triangle v2g IN[3], inout TriangleStream<g2f> stream) {
				g2f o;

                UNITY_SETUP_INSTANCE_ID(IN[0]);
                UNITY_INITIALIZE_OUTPUT(g2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

				if(IN[0].uv.x + IN[2].uv.x > IN[1].uv.x * 2) return;

				float3 p = IN[0].vertex.xyz, v = IN[1].vertex.xyz;
				v -= p;

                o.color = IN[0].color;

				float4 vp1 = UnityObjectToClipPos(float4(p, 1));
				float4 vp2 = UnityObjectToClipPos(float4(p + v, 1));
				float2 vd = vp1.xy / vp1.w - vp2.xy / vp2.w;
				float aspectRatio = - UNITY_MATRIX_P[0][0] / UNITY_MATRIX_P[1][1];
				vd.x /= aspectRatio;
				o.d = length(vd);
				if(length(vd) < 0.0001) vd = float2(1,0);
				else vd = normalize(vd);
				float2 vn = vd.yx * float2(-1,1);

				//if(abs(UNITY_MATRIX_P[0][2]) < 0.01) size *= 2; 
				float sz = _Width;
				vn *= sz;
				vn.x *= aspectRatio;
				
				if(length(v) < _Invisible) {
					o.d = 0;
					o.uv = float2(-1,-1);
					o.vertex = vp1+float4(+vn,0,0);
					stream.Append(o);
					o.uv = float2(-1,1);
					o.vertex = vp1+float4(-vn,0,0);
					stream.Append(o);
					o.uv = float2(1,-1);
					o.vertex = vp2+float4(+vn,0,0);
					stream.Append(o);
					o.uv = float2(1,1);
					o.vertex = vp2+float4(-vn,0,0);
					stream.Append(o);
					stream.RestartStrip();
				}
				
				o.d = 1;
				sz *= 2.0;
				if(IN[1].uv.x >= 0.999999) {
					o.uv = float2(0,1);
					o.vertex = vp2+float4(o.uv*sz*float2(aspectRatio,1),0,0);
					stream.Append(o);
					o.uv = float2(-0.9,-0.5);
					o.vertex = vp2+float4(o.uv*sz*float2(aspectRatio,1),0,0);
					stream.Append(o);
					o.uv = float2(0.9,-0.5);
					o.vertex = vp2+float4(o.uv*sz*float2(aspectRatio,1),0,0);
					stream.Append(o);
					stream.RestartStrip();
				}

				o.uv = float2(0,1);
				o.vertex = vp1+float4(o.uv*sz*float2(aspectRatio,1),0,0);
				stream.Append(o);
				o.uv = float2(-0.9,-0.5);
				o.vertex = vp1+float4(o.uv*sz*float2(aspectRatio,1),0,0);
				stream.Append(o);
				o.uv = float2(0.9,-0.5);
				o.vertex = vp1+float4(o.uv*sz*float2(aspectRatio,1),0,0);
				stream.Append(o);
				stream.RestartStrip();
			}
			
			fixed4 frag (g2f i) : SV_Target
			{
                // UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

				float l = length(i.uv);
				clip(- min(i.d - 0.5, l - 0.5));
				return float4(i.color * _Color.xyz, 1);
			}
			ENDCG
		}
	}
}
