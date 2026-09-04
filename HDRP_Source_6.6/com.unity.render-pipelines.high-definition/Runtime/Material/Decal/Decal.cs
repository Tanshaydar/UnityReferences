using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    partial class Decal
    {
        // Main structure that store the user data (i.e user input of master node in material graph)
        [GenerateHLSL(PackingRules.Exact, false)]
        public struct DecalSurfaceData
        {
            [SurfaceDataAttributes("Base Color", false, true)]
            public Vector4 baseColor;
            [SurfaceDataAttributes("Normal", true)]
            public Vector4 normalWS;
            [SurfaceDataAttributes("Mask", true)]
            public Vector4 mask; // Metal, AmbientOcclusion, Smoothness, smoothness opacity
            [SurfaceDataAttributes("Emissive")]
            public Vector3 emissive;
            [SurfaceDataAttributes("AOSBlend", true)]
            public Vector2 MAOSBlend; // Metal opacity and Ambient occlusion opacity
        };

        [GenerateHLSL(PackingRules.Exact)]
        public enum DBufferMaterial
        {
            Count = 4
        };

        [GenerateHLSL]
        public enum DecalAtlasTextureType
        {
            Diffuse,
            Normal,
            Mask,
            Count
        }

        //-----------------------------------------------------------------------------
        // DBuffer management
        //-----------------------------------------------------------------------------

        // should this be combined into common class shared with Lit.cs???
        public static int GetMaterialDBufferCount() => (int)DBufferMaterial.Count;

        const int k_RTFormatMask = (int)GraphicsFormat.R8G8B8A8_SRGB << 0 | (int)GraphicsFormat.R8G8B8A8_UNorm << 8 | (int)GraphicsFormat.R8G8B8A8_UNorm << 16 | (int)GraphicsFormat.R8G8_UNorm << 24;
        const int k_RTFormatMaskHP = (int)GraphicsFormat.R8G8B8A8_SRGB << 0 | (int)GraphicsFormat.R16G16B16A16_SFloat << 8 | (int)GraphicsFormat.R8G8B8A8_UNorm << 16 | (int)GraphicsFormat.R8G8_UNorm << 24;

        public static GraphicsFormat GetMaterialDBufferDescription(int index, bool useHighPrecision) => (GraphicsFormat)(0xFF & (useHighPrecision ? k_RTFormatMaskHP : k_RTFormatMask ) >> (index << 3));
    }

    // normal to world only uses 3x3 for actual matrix so some data is packed in the unused space
    // blend:
    // float decalBlend = decalData.normalToWorld[0][3];
    // albedo contribution on/off:
    // float albedoContribution = decalData.normalToWorld[1][3];
    // tiling:
    // float2 uvScale = float2(decalData.normalToWorld[3][0], decalData.normalToWorld[3][1]);
    // float2 uvBias = float2(decalData.normalToWorld[3][2], decalData.normalToWorld[3][3]);
    [GenerateHLSL(PackingRules.Exact, false)]
    struct DecalData
    {
        public Matrix4x4 worldToDecal;
        public Matrix4x4 normalToWorld;
        public Vector4 diffuseScaleBias;
        public Vector4 normalScaleBias;
        public Vector4 maskScaleBias;
        public Vector4 baseColor;
        public Vector4 remappingAOS;
        public Vector2 remappingMetallic;   // (min, max) or metallic value if no texture is used
        public float scalingBlueMaskMap;
        public float sampleNormalAlpha;
        public Vector3 blendParams; // x normal blend source, y mask blend source, z mask blend mode
        public uint decalLayerMask;
    };
}
