using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        private static LocalKeyword s_BigTileVolumetricLightListKeyword;
        const int kNativeLightListIndirectEventId = 0x10021;
        const int kNativeLightListIndirectWaitEventId = 0x10022;
        // PrePass event IDs (2026-05-12) — fired BEFORE BuildGPULightList
        // AddComputePass so native ScreenAABB + BigTile write the inputs that
        // HDRP's Voxel kernel reads inside that pass. Render-graph orders our
        // PrePass first via the Write→Read dependency on AABBBoundsBuffer +
        // bigTileLightList declared on both passes' builder.UseBuffer calls.
        const int kNativeLightListPrePassEventId = 0x10023;
        const int kNativeLightListPrePassWaitEventId = 0x10024;
        const int kNativeLightListIndirectParamsAbiSize = 416;
        // Bit layout for NativeLightListIndirectParams.clusteredDispatchFlags.
        // MUST stay in sync with the native kClusteredFlag* constants in
        // NativeLightList.cpp. bit0=dispatch runs, bit1=live native clustered,
        // bit2=parity/validation probe.
        const uint kClusteredFlagRunDispatch = 1u << 0;
        const uint kClusteredFlagIsLive = 1u << 1;
        const uint kClusteredFlagIsParityProbe = 1u << 2;
        const int kNativeContactShadowsEventId = 0x10031;
        const int kNativeContactShadowsWaitEventId = 0x10032;
        const int kNativeContactShadowsParamsAbiSize = 400;

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NativeLightListIndirectParams
        {
            public IntPtr tileFeatureFlags;
            public IntPtr tileList;
            public IntPtr dispatchIndirectBuffer;
            public IntPtr gbuffer2;
            public IntPtr stencil;
            public IntPtr aabbBounds;
            public IntPtr convexBounds;
            public IntPtr lightVolumeData;
            public IntPtr bigTileLightList;
            public IntPtr depth;
            public IntPtr lightList;
            public uint numTiles;
            public uint numTilesX;
            public uint viewCount;
            public uint frameIndex;
            public uint width;
            public uint height;
            public uint baseFeatureFlags;
            public uint materialFlagsMode;
            public uint numTilesY;
            public uint totalLightCount;
            public uint nativeFptl;
            public uint useBigTileForFptl;
            public uint fptlBaseFeatureFlags;
            public uint envLightIndexShift;
            public uint decalIndexShift;
            public float fptlInvM22;
            public float fptlInvM23;
            public float fptlInvM32;
            public float fptlInvM33;
            public float fptlScrSx;
            public float fptlScrSy;
            public float fptlScrCx;
            public float fptlScrCy;
            public float fptlScrCxOrtho;
            public float fptlScrCyOrtho;
            public uint fptlIsOrthographic;
            public uint fptlPad0;
            // BigTile native-replacement gate (audit follow-up 2026-05-12).
            // 1 = native plugin runs NativeLightListBigTile dispatch on the
            // injected compute queue and the C# side skips HDRP's
            // BigTilePrepass dispatch. When HDRP allocates the optional
            // volumetric BigTile output, the native shader fills it too.
            public uint nativeBigTile;
            public uint bigTilePad0;
            // ScreenAABB (scrbound) native-replacement gate (2026-05-12).
            // 1 = native ScreenAABB dispatches in PrePass, HDRP's
            // GenerateLightsScreenSpaceAABBs is skipped.
            public uint nativeScreenAABB;
            public uint screenAABBPad0;
            // Projection matrix for native ScreenAABB, packed in GLSL
            // column-major order. The shader derives invProj with inverse()
            // so we do not depend on a per-frame GPU matrix upload crossing
            // from Unity's graphics path to the native compute queue.
            public float screenAABBProjM00;
            public float screenAABBProjM10;
            public float screenAABBProjM20;
            public float screenAABBProjM30;
            public float screenAABBProjM01;
            public float screenAABBProjM11;
            public float screenAABBProjM21;
            public float screenAABBProjM31;
            public float screenAABBProjM02;
            public float screenAABBProjM12;
            public float screenAABBProjM22;
            public float screenAABBProjM32;
            public float screenAABBProjM03;
            public float screenAABBProjM13;
            public float screenAABBProjM23;
            public float screenAABBProjM33;
            // Kept for ABI stability with older native builds. Current native
            // ScreenAABB ignores this pointer.
            public IntPtr matricesBuffer;
            // BigTile volumetric output (full-faithful BigTile port,
            // 2026-05-12 second pass). When supportsVolumetric is true on
            // the HDRP asset, HDRP allocates output.bigTileVolumetricLightList
            // and we pass its native pointer here; native BigTile fills it
            // alongside the regular bigTileLightList. When false, this is
            // IntPtr.Zero and the native dispatch aliases binding 4 to the
            // regular bigTileLightList (the shader gates writes).
            public IntPtr bigTileVolumetricLightList;
            // Camera near/far plane for EXACT_EDGE_TESTS frustum vertex
            // construction in BigTile shader.
            public float nearPlane;
            public float farPlane;
            // 1 when HDRP feature-variant tile lists are enabled. Native FPTL
            // can run without this; the native plugin only records the
            // material/build-indirect kernels when this is non-zero.
            public uint enableFeatureVariants;
            public uint paramsPad0;
            // Offline parity probe: run native FPTL into scratch buffers while
            // stock HDRP remains the live lighting path.
            public IntPtr parityLightList;
            public IntPtr parityTileFeatureFlags;
            public uint parityFptl;
            public uint parityPad0;
            // Offline parity probe: native clustered/voxel generation writes
            // scratch PerVoxel buffers while stock HDRP remains live.
            public IntPtr parityClusteredLightList;
            public IntPtr parityClusteredOffset;
            public IntPtr parityClusteredLogBase;
            public IntPtr parityClusteredAtomic;
            // Clustered dispatch control + telemetry, bit-packed (2026-05-22
            // cleanup). Reinterprets the former `parityClustered uint` slot in
            // place — same offset, ABI-neutral, struct size unchanged. The old
            // name lied (set to 1 whenever clustered dispatched, live OR
            // parity). Bits: 0=runClusteredDispatch, 1=clusteredIsLive,
            // 2=clusteredIsParityProbe. Native side decodes via
            // kClusteredFlag* constants in NativeLightList.cpp.
            public uint clusteredDispatchFlags;
            public uint clusterNumTilesX;
            public uint clusterNumTilesY;
            public uint clusterNeedsDepth;
            public uint clusterUseBigTile;
            public uint clusteredLayeredLightListEntryCount;
            public uint clusteredLayeredOffsetEntryCount;
            public uint clusteredLogBaseEntryCount;
            public uint clusteredLog2NumClusters;
            public float clusteredClustBase;
            public uint clusteredPad0;
            public uint clusteredPad1;
        }

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern void FSR3_NativeLightListIndirect_SetParams(NativeLightListIndirectParams parameters);

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FSR3_NativeLightListIndirect_GetDispatchCallback();

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FSR3_NativeLightListIndirect_GetWaitCallback();

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern void FSR3_NativeLightListPrePass_SetParams(NativeLightListIndirectParams parameters);

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FSR3_NativeLightListPrePass_GetDispatchCallback();

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FSR3_NativeLightListPrePass_GetWaitCallback();

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NativeContactShadowsParams
        {
            public IntPtr depth;
            public IntPtr output;
            public IntPtr lightList;
            public IntPtr lightData;
            public IntPtr directionalLightData;
            public uint width;
            public uint height;
            public uint viewCount;
            public uint frameIndex;
            public uint numTilesX;
            public uint numTilesY;
            public uint directionalLightCount;
            public uint punctualLightCount;
            public uint taaEnabled;
            public uint pad0;
            public Vector4 params1;
            public Vector4 params2;
            public Vector4 params3;
            public Vector4 screenSize;
            public Matrix4x4 invViewProj;
            public Matrix4x4 view;
            public Matrix4x4 viewProj;
            public Matrix4x4 proj;
        }

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern void FSR3_NativeContactShadows_SetParams(NativeContactShadowsParams parameters);

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FSR3_NativeContactShadows_GetDispatchCallback();

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FSR3_NativeContactShadows_GetWaitCallback();

        static void SetNativeScreenAABBProjection(ref NativeLightListIndirectParams parameters, Matrix4x4 projection)
        {
            parameters.screenAABBProjM00 = projection.m00;
            parameters.screenAABBProjM10 = projection.m10;
            parameters.screenAABBProjM20 = projection.m20;
            parameters.screenAABBProjM30 = projection.m30;
            parameters.screenAABBProjM01 = projection.m01;
            parameters.screenAABBProjM11 = projection.m11;
            parameters.screenAABBProjM21 = projection.m21;
            parameters.screenAABBProjM31 = projection.m31;
            parameters.screenAABBProjM02 = projection.m02;
            parameters.screenAABBProjM12 = projection.m12;
            parameters.screenAABBProjM22 = projection.m22;
            parameters.screenAABBProjM32 = projection.m32;
            parameters.screenAABBProjM03 = projection.m03;
            parameters.screenAABBProjM13 = projection.m13;
            parameters.screenAABBProjM23 = projection.m23;
            parameters.screenAABBProjM33 = projection.m33;
        }

        static bool s_NativeLightListIndirectProbeAttempted;
        static IntPtr s_NativeLightListIndirectCallback = IntPtr.Zero;
        static IntPtr s_NativeLightListIndirectWaitCallback = IntPtr.Zero;
        static bool s_NativeLightListIndirectFallbackLogged;
        static bool s_NativeLightListVisualParityHoldLogged;
        static bool s_NativeLightListDiagnosticModeLogged;
        static bool s_NativeLightListSkipStockClusteredLogged;
        static bool s_NativeLightListIndirectReadyLogged;
        static bool s_NativeLightListPrePassReadyLogged;
        static bool s_NativeLightListParamsAbiChecked;
        static bool s_NativeLightListParamsAbiSupported;
        static int s_NativeLightListIndirectLogCounter;
        static int s_NativeLightListFptlParityRequestCounter;
        static int s_NativeLightListClusteredParityRequestCounter;
        static readonly Dictionary<string, int> s_NativeAsyncCandidateLastLogFrame = new Dictionary<string, int>();
        const int kNativeLightListFptlParityReadbackInterval = 120;
        const int kNativeLightListClusteredParityReadbackInterval = 120;
        const int kNativeAsyncCandidateLogInterval = 120;

        static void LogNativeAsyncCandidate(string passName, string details)
        {
            int frame = Time.frameCount;
            if (s_NativeAsyncCandidateLastLogFrame.TryGetValue(passName, out int lastFrame) &&
                frame - lastFrame < kNativeAsyncCandidateLogInterval)
            {
                return;
            }

            s_NativeAsyncCandidateLastLogFrame[passName] = frame;
            Debug.Log($"[FSR3DeckNativeAttribution] pass={passName} frame={frame} {details}");
        }

        static bool NativeLightListParamsAbiSupported()
        {
            if (!s_NativeLightListParamsAbiChecked)
            {
                s_NativeLightListParamsAbiChecked = true;
                int managedSize = Marshal.SizeOf(typeof(NativeLightListIndirectParams));
                s_NativeLightListParamsAbiSupported =
                    managedSize == kNativeLightListIndirectParamsAbiSize &&
                    IntPtr.Size == 8;

                if (s_NativeLightListParamsAbiSupported)
                {
                    Debug.Log($"[FSR3DeckNativeLightList] Managed ABI OK: NativeLightListIndirectParams={managedSize} bytes, pointerSize={IntPtr.Size}.");
                }
                else
                {
                    Debug.LogError($"[FSR3DeckNativeLightList] Managed ABI mismatch: NativeLightListIndirectParams={managedSize} bytes, expected={kNativeLightListIndirectParamsAbiSize}, pointerSize={IntPtr.Size}. Native light-list replacement disabled.");
                }
            }

            return s_NativeLightListParamsAbiSupported;
        }

        sealed class NativeLightListFptlParitySample
        {
            public uint[] stock;
            public uint[] native;
            public int compareDwords;
            public int numTiles;
            public int numTilesX;
            public int viewCount;
            public int totalLightCount;
        }

        struct NativeLightListFptlParityMismatch
        {
            public string reason;
            public int dword;
            public int dwordInTile;
            public int eye;
            public int category;
            public int tile;
            public int tileX;
            public int tileY;
            public int lightOrdinal;
            public int stockCount;
            public int nativeCount;
            public uint stockValue;
            public uint nativeValue;
            public uint stockRaw;
            public uint nativeRaw;
        }

        static readonly object s_NativeLightListFptlParityLock = new object();
        static readonly Dictionary<uint, NativeLightListFptlParitySample> s_NativeLightListFptlParitySamples =
            new Dictionary<uint, NativeLightListFptlParitySample>();
        static int s_NativeLightListFptlParityMatches;
        static int s_NativeLightListFptlParityMismatches;

        sealed class NativeLightListClusteredParitySample
        {
            public uint[] stockOffset;
            public uint[] nativeOffset;
            public uint[] stockList;
            public uint[] nativeList;
            public int offsetDwords;
            public int listDwords;
            public int numTiles;
            public int numTilesX;
            public int numTilesY;
            public int viewCount;
            public int numClusters;
            public int totalLightCount;
        }

        struct NativeLightListClusteredParityMismatch
        {
            public string reason;
            public int offsetIndex;
            public int eye;
            public int category;
            public int cluster;
            public int tile;
            public int tileX;
            public int tileY;
            public int lightOrdinal;
            public int stockCount;
            public int nativeCount;
            public int stockStart;
            public int nativeStart;
            public uint stockValue;
            public uint nativeValue;
            public uint stockRaw;
            public uint nativeRaw;
        }

        static readonly object s_NativeLightListClusteredParityLock = new object();
        static readonly Dictionary<uint, NativeLightListClusteredParitySample> s_NativeLightListClusteredParitySamples =
            new Dictionary<uint, NativeLightListClusteredParitySample>();
        static int s_NativeLightListClusteredParityMatches;
        static int s_NativeLightListClusteredParityMismatches;

        // PrePass callback probes (2026-05-12).
        static bool s_NativeLightListPrePassProbeAttempted;
        static IntPtr s_NativeLightListPrePassCallback = IntPtr.Zero;
        static IntPtr s_NativeLightListPrePassWaitCallback = IntPtr.Zero;
        static bool s_NativeContactShadowsProbeAttempted;
        static IntPtr s_NativeContactShadowsCallback = IntPtr.Zero;
        static IntPtr s_NativeContactShadowsWaitCallback = IntPtr.Zero;
        static bool s_NativeContactShadowsReadyLogged;
        static bool s_NativeContactShadowsParamsAbiChecked;
        static bool s_NativeContactShadowsParamsAbiSupported;
        static int s_NativeContactShadowsLogCounter;

        enum NativeLightListDiagnosticMode
        {
            VisualParityHold = 0,
            ScreenAABBOnly = 1,
            FptlWithStockBounds = 2,
            MaterialIndirectWithStockFptl = 3,
            FullNativeChain = 4,
            FptlAndClusteredWithStockBounds = 5,
            FptlAndClusteredSkipStockVoxel = 6,
        }

        // Deck-only native light-list reintroduction switch. This is kept as a
        // source-level diagnostic selector instead of an environment variable:
        // the product goal is native async by default, but the lighting chain
        // now comes back one proven slice at a time.
        static readonly NativeLightListDiagnosticMode kNativeLightListDiagnosticMode =
            NativeLightListDiagnosticMode.FptlAndClusteredSkipStockVoxel;

        // Source-level parity harness for the material/indirect-only mode. It
        // does not affect pixels there: stock HDRP owns the live FPTL light
        // list while native FPTL writes scratch buffers and compares semantic
        // light-list contents after the native wait.
        static readonly bool kNativeLightListFptlParityProbeEnabled = true;
        static readonly bool kNativeLightListClusteredParityProbeEnabled = true;

        struct LightingBuffers
        {
            public TextureHandle sssBuffer;
            public TextureHandle diffuseLightingBuffer;

            public TextureHandle ambientOcclusionBuffer;
            public TextureHandle ssrLightingBuffer;
            public TextureHandle ssgiLightingBuffer;
            public TextureHandle contactShadowsBuffer;
            public TextureHandle screenspaceShadowBuffer;
        }

        static LightingBuffers ReadLightingBuffers(in LightingBuffers buffers, IUnsafeRenderGraphBuilder builder)
        {
            var result = new LightingBuffers();
            // We only read those buffers because sssBuffer and diffuseLightingBuffer our just output of the lighting process, not inputs.
            result.ambientOcclusionBuffer = buffers.ambientOcclusionBuffer;
            builder.UseTexture(result.ambientOcclusionBuffer, AccessFlags.Read);
            result.ssrLightingBuffer = buffers.ssrLightingBuffer;
            builder.UseTexture(result.ssrLightingBuffer, AccessFlags.Read);
            result.ssgiLightingBuffer = buffers.ssgiLightingBuffer;
            builder.UseTexture(result.ssgiLightingBuffer, AccessFlags.Read);
            result.contactShadowsBuffer = buffers.contactShadowsBuffer;
            builder.UseTexture(result.contactShadowsBuffer, AccessFlags.Read);
            result.screenspaceShadowBuffer = buffers.screenspaceShadowBuffer;
            builder.UseTexture(result.screenspaceShadowBuffer, AccessFlags.Read);

            return result;
        }

        static void BindGlobalLightingBuffers(in LightingBuffers buffers, CommandBuffer cmd)
        {
            cmd.SetGlobalTexture(HDShaderIDs._AmbientOcclusionTexture, buffers.ambientOcclusionBuffer);
            cmd.SetGlobalTexture(HDShaderIDs._SsrLightingTexture, buffers.ssrLightingBuffer);
            cmd.SetGlobalTexture(HDShaderIDs._IndirectDiffuseTexture, buffers.ssgiLightingBuffer);
            cmd.SetGlobalTexture(HDShaderIDs._ContactShadowTexture, buffers.contactShadowsBuffer);
            cmd.SetGlobalTexture(HDShaderIDs._ScreenSpaceShadowsTexture, buffers.screenspaceShadowBuffer);
        }

        static void BindGlobalThicknessBuffers(TextureHandle thicknessTexture, GraphicsBuffer thicknessReindexMap, CommandBuffer cmd)
        {
            cmd.SetGlobalTexture(HDShaderIDs._ThicknessTexture, thicknessTexture);
            cmd.SetGlobalBuffer(HDShaderIDs._ThicknessReindexMap, thicknessReindexMap);
        }

        static void BindDefaultTexturesLightingBuffers(RenderGraphDefaultResources defaultResources, CommandBuffer cmd)
        {
            cmd.SetGlobalTexture(HDShaderIDs._AmbientOcclusionTexture, defaultResources.blackTextureXR);
            cmd.SetGlobalTexture(HDShaderIDs._SsrLightingTexture, defaultResources.blackTextureXR);
            cmd.SetGlobalTexture(HDShaderIDs._IndirectDiffuseTexture, defaultResources.blackTextureXR);
            cmd.SetGlobalTexture(HDShaderIDs._ContactShadowTexture, defaultResources.blackUIntTextureXR);
            cmd.SetGlobalTexture(HDShaderIDs._ScreenSpaceShadowsTexture, defaultResources.blackTextureXR);
        }

        class BuildGPULightListPassData
        {
            // Common
            public int totalLightCount; // Regular + Env + Decal + Local Volumetric Fog
            public int viewCount;
            public bool runLightList;
            public bool clearLightLists;
            public bool enableFeatureVariants;
            public bool computeMaterialVariants;
            public bool computeLightVariants;
            public bool nativeIndirect;
            public bool nativeFptl;
            public bool nativeClustered;
            public bool nativeBigTile;
            public bool nativeScreenAABB;
            public bool nativeMaterialFlags;
            public bool nativeFptlProjectionSupported;
            public uint nativeFptlBaseFeatureFlags;
            public uint nativeMaterialBaseFeatureFlags;
            public uint nativeMaterialFlagsMode;
            public float nativeFptlInvM22;
            public float nativeFptlInvM23;
            public float nativeFptlInvM32;
            public float nativeFptlInvM33;
            public float nativeFptlScrSx;
            public float nativeFptlScrSy;
            public float nativeFptlScrCx;
            public float nativeFptlScrCy;
            public float nativeFptlScrCxOrtho;
            public float nativeFptlScrCyOrtho;
            public bool skyEnabled;
            public LightList lightList;
            public bool canClearLightList;
            public int directionalLightCount;

            // Clear Light lists
            public ComputeShader clearLightListCS;
            public int clearLightListKernel;

            // Screen Space AABBs
            public ComputeShader screenSpaceAABBShader;
            public int screenSpaceAABBKernel;

            // Big Tile
            public ComputeShader bigTilePrepassShader;
            public int bigTilePrepassKernel;
            public bool runBigTilePrepass;
            public int numBigTilesX, numBigTilesY;
            public bool supportsVolumetric;

            // FPTL
            public ComputeShader buildPerTileLightListShader;
            public int buildPerTileLightListKernel;
            public bool runFPTL;
            public int numTilesFPTLX;
            public int numTilesFPTLY;
            public int numTilesFPTL;

            // Cluster
            public ComputeShader buildPerVoxelLightListShader;
            public ComputeShader clearClusterAtomicIndexShader;
            public int buildPerVoxelLightListKernel;
            public int numTilesClusterX;
            public int numTilesClusterY;
            public bool clusterNeedsDepth;

            // Build dispatch indirect
            public ComputeShader buildMaterialFlagsShader;
            public ComputeShader clearDispatchIndirectShader;
            public ComputeShader buildDispatchIndirectShader;

            public ShaderVariablesLightList lightListCB;

            public TextureHandle depthBuffer;
            public TextureHandle stencilTexture;
            public TextureHandle[] gBuffer = new TextureHandle[RenderGraph.kMaxMRTCount];
            public int gBufferCount;

            // Buffers filled with the CPU outside of render graph.
            public BufferHandle convexBoundsBuffer;
            public BufferHandle AABBBoundsBuffer;

            // Transient buffers that are not used outside of BuildGPULight list so they don't need to go outside the pass.
            public BufferHandle globalLightListAtomic;
            public BufferHandle lightVolumeDataBuffer;

            public BuildGPULightListOutput output = new BuildGPULightListOutput();
        }

        internal struct BuildGPULightListOutput
        {
            // Tile
            public BufferHandle lightList;
            public BufferHandle tileList;
            public BufferHandle tileFeatureFlags;
            public BufferHandle dispatchIndirectBuffer;

            // Big Tile
            public BufferHandle bigTileLightList;
            public BufferHandle bigTileVolumetricLightList;

            // Cluster
            public BufferHandle perVoxelOffset;
            public BufferHandle perVoxelLightLists;
            public BufferHandle perTileLogBaseTweak;
        }

        static void ClearLightList(BuildGPULightListPassData data, ComputeCommandBuffer cmd, GraphicsBuffer bufferToClear)
        {
            cmd.SetComputeBufferParam(data.clearLightListCS, data.clearLightListKernel, HDShaderIDs._LightListToClear, bufferToClear);
            Vector2 countAndOffset = new Vector2Int(bufferToClear.count, 0);

            int groupSize = 64;
            int totalNumberOfGroupsNeeded = (bufferToClear.count + groupSize - 1) / groupSize;

            const int maxAllowedGroups = 65535;
            // On higher resolutions we might end up with more than 65535 group which is not allowed, so we need to to have multiple dispatches.
            int i = 0;
            while (totalNumberOfGroupsNeeded > 0)
            {
                countAndOffset.y = maxAllowedGroups * i;
                cmd.SetComputeVectorParam(data.clearLightListCS, HDShaderIDs._LightListEntriesAndOffset, countAndOffset);

                int currGroupCount = Math.Min(maxAllowedGroups, totalNumberOfGroupsNeeded);

                cmd.DispatchCompute(data.clearLightListCS, data.clearLightListKernel, currGroupCount, 1, 1);

                totalNumberOfGroupsNeeded -= currGroupCount;
                i++;
            }
        }

        static void ClearLightLists(BuildGPULightListPassData data, ComputeCommandBuffer cmd)
        {
            if (data.clearLightLists)
            {
                // Note we clear the whole content and not just the header since it is fast enough, happens only in one frame and is a bit more robust
                // to changes to the inner workings of the lists.
                // Also, we clear all the lists and to be resilient to changes in pipeline.
                if (data.runBigTilePrepass)
                {
                    ClearLightList(data, cmd, data.output.bigTileLightList);
                    if (data.supportsVolumetric)
                        ClearLightList(data, cmd, data.output.bigTileVolumetricLightList);
                }
                if (data.canClearLightList) // This can happen when we dont have a GPULight list builder and a light list instantiated.
                    ClearLightList(data, cmd, data.output.lightList);
                ClearLightList(data, cmd, data.output.perVoxelOffset);
            }
        }

        // generate screen-space AABBs (used for both fptl and clustered).
        static void GenerateLightsScreenSpaceAABBs(BuildGPULightListPassData data, ComputeCommandBuffer cmd, bool nativeScreenAABB)
        {
            if (nativeScreenAABB)
            {
                // Native ScreenAABB ran in the PrePass before this AddComputePass.
                // The output AABBBoundsBuffer is already populated; HDRP-stock
                // dispatch is skipped.
                return;
            }
            if (data.totalLightCount != 0)
            {
                using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.GenerateLightAABBs)))
                {
                    // With XR single-pass, we have one set of light bounds per view to iterate over (bounds are in view space for each view)
                    cmd.SetComputeBufferParam(data.screenSpaceAABBShader, data.screenSpaceAABBKernel, HDShaderIDs.g_data, data.convexBoundsBuffer);
                    cmd.SetComputeBufferParam(data.screenSpaceAABBShader, data.screenSpaceAABBKernel, HDShaderIDs.g_vBoundsBuffer, data.AABBBoundsBuffer);

                    ConstantBuffer.Push(cmd, data.lightListCB, data.screenSpaceAABBShader, HDShaderIDs._ShaderVariablesLightList);

                    const int threadsPerLight = 4;  // Shader: THREADS_PER_LIGHT (4)
                    const int threadsPerGroup = 64; // Shader: THREADS_PER_GROUP (64)

                    int groupCount = HDUtils.DivRoundUp(data.totalLightCount * threadsPerLight, threadsPerGroup);

                    cmd.DispatchCompute(data.screenSpaceAABBShader, data.screenSpaceAABBKernel, groupCount, data.viewCount, 1);
                }
            }
        }

        // enable coarse 2D pass on 64x64 tiles (used for both fptl and clustered).
        static void BigTilePrepass(BuildGPULightListPassData data, ComputeCommandBuffer cmd, bool nativeBigTile)
        {
            if (nativeBigTile)
            {
                // Native plugin path: NativeLightListBigTile dispatch runs on the
                // injected compute queue from RenderNativeLightListIndirect; the
                // HDRP-stock dispatch below is skipped entirely. The PrePass
                // writes bigTileVolumetricLightList too when HDRP allocated it.
                return;
            }
            if (data.runLightList && data.runBigTilePrepass)
            {
                cmd.SetComputeBufferParam(data.bigTilePrepassShader, data.bigTilePrepassKernel, HDShaderIDs.g_vLightList, data.output.bigTileLightList);
                cmd.SetKeyword(data.bigTilePrepassShader, s_BigTileVolumetricLightListKeyword, data.supportsVolumetric);
                if (data.supportsVolumetric)
                    cmd.SetComputeBufferParam(data.bigTilePrepassShader, data.bigTilePrepassKernel, HDShaderIDs.g_vVolumetricLightList, data.output.bigTileVolumetricLightList);
                cmd.SetComputeBufferParam(data.bigTilePrepassShader, data.bigTilePrepassKernel, HDShaderIDs.g_vBoundsBuffer, data.AABBBoundsBuffer);
                cmd.SetComputeBufferParam(data.bigTilePrepassShader, data.bigTilePrepassKernel, HDShaderIDs._LightVolumeData, data.lightVolumeDataBuffer);
                cmd.SetComputeBufferParam(data.bigTilePrepassShader, data.bigTilePrepassKernel, HDShaderIDs.g_data, data.convexBoundsBuffer);

                ConstantBuffer.Push(cmd, data.lightListCB, data.bigTilePrepassShader, HDShaderIDs._ShaderVariablesLightList);

                cmd.DispatchCompute(data.bigTilePrepassShader, data.bigTilePrepassKernel, data.numBigTilesX, data.numBigTilesY, data.viewCount);
            }
        }

        static void BuildPerTileLightList(BuildGPULightListPassData data, ref bool tileFlagsWritten, bool nativeFptl, ComputeCommandBuffer cmd)
        {
            if (nativeFptl)
            {
                tileFlagsWritten = data.enableFeatureVariants;
                return;
            }

            // optimized for opaques only
            if (data.runLightList && data.runFPTL)
            {
                cmd.SetComputeBufferParam(data.buildPerTileLightListShader, data.buildPerTileLightListKernel, HDShaderIDs.g_vBoundsBuffer, data.AABBBoundsBuffer);
                cmd.SetComputeBufferParam(data.buildPerTileLightListShader, data.buildPerTileLightListKernel, HDShaderIDs._LightVolumeData, data.lightVolumeDataBuffer);
                cmd.SetComputeBufferParam(data.buildPerTileLightListShader, data.buildPerTileLightListKernel, HDShaderIDs.g_data, data.convexBoundsBuffer);

                cmd.SetComputeTextureParam(data.buildPerTileLightListShader, data.buildPerTileLightListKernel, HDShaderIDs.g_depth_tex, data.depthBuffer);
                cmd.SetComputeBufferParam(data.buildPerTileLightListShader, data.buildPerTileLightListKernel, HDShaderIDs.g_vLightList, data.output.lightList);
                if (data.runBigTilePrepass)
                    cmd.SetComputeBufferParam(data.buildPerTileLightListShader, data.buildPerTileLightListKernel, HDShaderIDs.g_vBigTileLightList, data.output.bigTileLightList);

                var localLightListCB = data.lightListCB;

                if (data.enableFeatureVariants)
                {
                    uint baseFeatureFlags = 0;
                    if (data.directionalLightCount > 0)
                    {
                        baseFeatureFlags |= (uint)LightFeatureFlags.Directional;
                    }
                    if (data.skyEnabled)
                    {
                        baseFeatureFlags |= (uint)LightFeatureFlags.Sky;
                    }
                    if (!data.computeMaterialVariants)
                    {
                        baseFeatureFlags |= LightDefinitions.s_MaterialFeatureMaskFlags;
                    }

                    localLightListCB.g_BaseFeatureFlags = baseFeatureFlags;

                    cmd.SetComputeBufferParam(data.buildPerTileLightListShader, data.buildPerTileLightListKernel, HDShaderIDs.g_TileFeatureFlags, data.output.tileFeatureFlags);
                    tileFlagsWritten = true;
                }

                ConstantBuffer.Push(cmd, localLightListCB, data.buildPerTileLightListShader, HDShaderIDs._ShaderVariablesLightList);

                cmd.DispatchCompute(data.buildPerTileLightListShader, data.buildPerTileLightListKernel, data.numTilesFPTLX, data.numTilesFPTLY, data.viewCount);
            }
        }

        static void VoxelLightListGeneration(BuildGPULightListPassData data, ComputeCommandBuffer cmd)
        {
            if (data.runLightList)
            {
                // clear atomic offset index
                cmd.SetComputeBufferParam(data.clearClusterAtomicIndexShader, s_ClearVoxelAtomicKernel, HDShaderIDs.g_LayeredSingleIdxBuffer, data.globalLightListAtomic);
                cmd.DispatchCompute(data.clearClusterAtomicIndexShader, s_ClearVoxelAtomicKernel, 1, 1, 1);

                cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, s_ClearVoxelAtomicKernel, HDShaderIDs.g_LayeredSingleIdxBuffer, data.globalLightListAtomic);
                cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs.g_vLayeredLightList, data.output.perVoxelLightLists);
                cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs.g_LayeredOffset, data.output.perVoxelOffset);
                cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs.g_LayeredSingleIdxBuffer, data.globalLightListAtomic);

                if (data.runBigTilePrepass)
                    cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs.g_vBigTileLightList, data.output.bigTileLightList);

                if (data.clusterNeedsDepth)
                {
                    cmd.SetComputeTextureParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs.g_depth_tex, data.depthBuffer);
                    cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs.g_logBaseBuffer, data.output.perTileLogBaseTweak);
                }

                cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs.g_vBoundsBuffer, data.AABBBoundsBuffer);
                cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs._LightVolumeData, data.lightVolumeDataBuffer);
                cmd.SetComputeBufferParam(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, HDShaderIDs.g_data, data.convexBoundsBuffer);

                ConstantBuffer.Push(cmd, data.lightListCB, data.buildPerVoxelLightListShader, HDShaderIDs._ShaderVariablesLightList);

                cmd.DispatchCompute(data.buildPerVoxelLightListShader, data.buildPerVoxelLightListKernel, data.numTilesClusterX, data.numTilesClusterY, data.viewCount);
            }
        }

        static void BuildDispatchIndirectArguments(BuildGPULightListPassData data, bool tileFlagsWritten, bool nativeIndirect, bool nativeMaterialFlags, ComputeCommandBuffer cmd)
        {
            if (data.enableFeatureVariants)
            {
                // We need to touch up the tile flags if we need material classification or, if disabled, to patch up for missing flags during the skipped light tile gen
                bool needModifyingTileFeatures = !tileFlagsWritten || data.computeMaterialVariants;
                if (needModifyingTileFeatures && !nativeMaterialFlags)
                {
                    int buildMaterialFlagsKernel = s_BuildMaterialFlagsWriteKernel;
                    data.buildMaterialFlagsShader.shaderKeywords = null;
                    if (tileFlagsWritten && data.computeLightVariants)
                    {
                        data.buildMaterialFlagsShader.EnableKeyword("USE_OR");
                    }

                    uint baseFeatureFlags = 0;
                    if (!data.computeLightVariants)
                    {
                        baseFeatureFlags |= LightDefinitions.s_LightFeatureMaskFlags;
                    }

                    // If we haven't run the light list building, we are missing some basic lighting flags.
                    if (!tileFlagsWritten)
                    {
                        if (data.directionalLightCount > 0)
                        {
                            baseFeatureFlags |= (uint)LightFeatureFlags.Directional;
                        }
                        if (data.skyEnabled)
                        {
                            baseFeatureFlags |= (uint)LightFeatureFlags.Sky;
                        }
                        if (!data.computeMaterialVariants)
                        {
                            baseFeatureFlags |= LightDefinitions.s_MaterialFeatureMaskFlags;
                        }
                    }

                    var localLightListCB = data.lightListCB;
                    localLightListCB.g_BaseFeatureFlags = baseFeatureFlags;

                    cmd.SetComputeBufferParam(data.buildMaterialFlagsShader, buildMaterialFlagsKernel, HDShaderIDs.g_TileFeatureFlags, data.output.tileFeatureFlags);

                    for (int i = 0; i < data.gBufferCount; ++i)
                        cmd.SetComputeTextureParam(data.buildMaterialFlagsShader, buildMaterialFlagsKernel, HDShaderIDs._GBufferTexture[i], data.gBuffer[i]);

                    RTHandle stencilTexture = data.stencilTexture;
                    if (stencilTexture?.rt != null && stencilTexture.rt.stencilFormat != GraphicsFormat.None)
                    {
                        cmd.SetComputeTextureParam(data.buildMaterialFlagsShader, buildMaterialFlagsKernel, HDShaderIDs._StencilTexture, data.stencilTexture, 0, RenderTextureSubElement.Stencil);
                    }
                    else // We are accessing MSAA resolved version or default black texture and not the depth stencil buffer directly.
                    {
                        cmd.SetComputeTextureParam(data.buildMaterialFlagsShader, buildMaterialFlagsKernel, HDShaderIDs._StencilTexture, data.stencilTexture);
                    }

                    ConstantBuffer.Push(cmd, localLightListCB, data.buildMaterialFlagsShader, HDShaderIDs._ShaderVariablesLightList);

                    cmd.DispatchCompute(data.buildMaterialFlagsShader, buildMaterialFlagsKernel, data.numTilesFPTLX, data.numTilesFPTLY, data.viewCount);
                }

                if (nativeIndirect)
                    return;

                // clear dispatch indirect buffer
                cmd.SetComputeBufferParam(data.clearDispatchIndirectShader, s_ClearDispatchIndirectKernel, HDShaderIDs.g_DispatchIndirectBuffer, data.output.dispatchIndirectBuffer);
                cmd.DispatchCompute(data.clearDispatchIndirectShader, s_ClearDispatchIndirectKernel, HDUtils.DivRoundUp(LightDefinitions.s_NumFeatureVariants, k_ThreadGroupOptimalSize), 1, data.viewCount);

                // add tiles to indirect buffer
                cmd.SetComputeBufferParam(data.buildDispatchIndirectShader, s_BuildIndirectKernel, HDShaderIDs.g_DispatchIndirectBuffer, data.output.dispatchIndirectBuffer);
                cmd.SetComputeBufferParam(data.buildDispatchIndirectShader, s_BuildIndirectKernel, HDShaderIDs.g_TileList, data.output.tileList);
                cmd.SetComputeBufferParam(data.buildDispatchIndirectShader, s_BuildIndirectKernel, HDShaderIDs.g_TileFeatureFlags, data.output.tileFeatureFlags);
                cmd.SetComputeIntParam(data.buildDispatchIndirectShader, HDShaderIDs.g_NumTiles, data.numTilesFPTL);
                cmd.SetComputeIntParam(data.buildDispatchIndirectShader, HDShaderIDs.g_NumTilesX, data.numTilesFPTLX);
                // Round on k_ThreadGroupOptimalSize so we have optimal thread for buildDispatchIndirectShader kernel
                cmd.DispatchCompute(data.buildDispatchIndirectShader, s_BuildIndirectKernel, (data.numTilesFPTL + k_ThreadGroupOptimalSize - 1) / k_ThreadGroupOptimalSize, 1, data.viewCount);
            }
        }

        static bool NativeLightListIndirectRuntimeSupported()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan &&
                (Application.platform == RuntimePlatform.LinuxPlayer ||
                 Application.platform == RuntimePlatform.LinuxEditor);
        }

        static IntPtr GetNativeLightListIndirectDispatchCallback()
        {
            if (!NativeLightListIndirectRuntimeSupported())
                return IntPtr.Zero;
            if (!NativeLightListParamsAbiSupported())
                return IntPtr.Zero;

            if (!s_NativeLightListIndirectProbeAttempted)
            {
                s_NativeLightListIndirectProbeAttempted = true;
                try
                {
                    s_NativeLightListIndirectCallback = FSR3_NativeLightListIndirect_GetDispatchCallback();
                    s_NativeLightListIndirectWaitCallback = FSR3_NativeLightListIndirect_GetWaitCallback();
                }
                catch (DllNotFoundException)
                {
                    s_NativeLightListIndirectCallback = IntPtr.Zero;
                    s_NativeLightListIndirectWaitCallback = IntPtr.Zero;
                }
                catch (EntryPointNotFoundException)
                {
                    s_NativeLightListIndirectCallback = IntPtr.Zero;
                    s_NativeLightListIndirectWaitCallback = IntPtr.Zero;
                }
            }

            return s_NativeLightListIndirectCallback;
        }

        // Phase 1f-C diagnostic (2026-05-18): set true to disable native
        // LightList Indirect + ContactShadows for the FSR3-isolation A/B
        // benchmark. Stock HDRP BuildGPULightList runs on graphics queue
        // instead. Used to test whether FSR3 slot-0 kernel variability is
        // caused by CU/memory contention from concurrent native family-1
        // dispatches. Flip back to false to restore.
        const bool kNativeUpscalerOnlyDiagnostic_LightAndShadows = false;

        static bool NativeLightListIndirectReady()
        {
            if (kNativeUpscalerOnlyDiagnostic_LightAndShadows) return false;
            bool ready = GetNativeLightListIndirectDispatchCallback() != IntPtr.Zero &&
                s_NativeLightListIndirectWaitCallback != IntPtr.Zero;
            if (ready && !s_NativeLightListIndirectReadyLogged)
            {
                s_NativeLightListIndirectReadyLogged = true;
                Debug.Log("[FSR3DeckNativeLightList] Native indirect callbacks ready.");
            }

            return ready;
        }

        static bool NativeContactShadowsParamsAbiSupported()
        {
            if (!s_NativeContactShadowsParamsAbiChecked)
            {
                s_NativeContactShadowsParamsAbiChecked = true;
                int managedSize = Marshal.SizeOf(typeof(NativeContactShadowsParams));
                s_NativeContactShadowsParamsAbiSupported =
                    managedSize == kNativeContactShadowsParamsAbiSize &&
                    IntPtr.Size == 8;

                if (s_NativeContactShadowsParamsAbiSupported)
                {
                    Debug.Log($"[FSR3DeckNativeContactShadows] Managed ABI OK: NativeContactShadowsParams={managedSize} bytes, pointerSize={IntPtr.Size}.");
                }
                else
                {
                    Debug.LogError($"[FSR3DeckNativeContactShadows] Managed ABI mismatch: NativeContactShadowsParams={managedSize} bytes, expected={kNativeContactShadowsParamsAbiSize}, pointerSize={IntPtr.Size}. Native contact shadows disabled.");
                }
            }

            return s_NativeContactShadowsParamsAbiSupported;
        }

        static bool NativeContactShadowsRuntimeSupported()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan &&
                (Application.platform == RuntimePlatform.LinuxPlayer ||
                 Application.platform == RuntimePlatform.LinuxEditor);
        }

        static IntPtr GetNativeContactShadowsDispatchCallback()
        {
            if (!NativeContactShadowsRuntimeSupported())
                return IntPtr.Zero;
            if (!NativeContactShadowsParamsAbiSupported())
                return IntPtr.Zero;

            if (!s_NativeContactShadowsProbeAttempted)
            {
                s_NativeContactShadowsProbeAttempted = true;
                try
                {
                    s_NativeContactShadowsCallback = FSR3_NativeContactShadows_GetDispatchCallback();
                    s_NativeContactShadowsWaitCallback = FSR3_NativeContactShadows_GetWaitCallback();
                }
                catch (DllNotFoundException)
                {
                    s_NativeContactShadowsCallback = IntPtr.Zero;
                    s_NativeContactShadowsWaitCallback = IntPtr.Zero;
                }
                catch (EntryPointNotFoundException)
                {
                    s_NativeContactShadowsCallback = IntPtr.Zero;
                    s_NativeContactShadowsWaitCallback = IntPtr.Zero;
                }
            }

            return s_NativeContactShadowsCallback;
        }

        static bool NativeContactShadowsReady()
        {
            // Phase 1f-C diagnostic: same gate as LightList Indirect.
            if (kNativeUpscalerOnlyDiagnostic_LightAndShadows) return false;
            bool ready = GetNativeContactShadowsDispatchCallback() != IntPtr.Zero &&
                s_NativeContactShadowsWaitCallback != IntPtr.Zero;
            if (ready && !s_NativeContactShadowsReadyLogged)
            {
                s_NativeContactShadowsReadyLogged = true;
                Debug.Log("[FSR3DeckNativeContactShadows] Native callbacks ready.");
            }

            return ready;
        }

        static bool NativeLightListVisualParityHold()
        {
            return kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.VisualParityHold;
        }

        static bool NativeLightListDiagnosticAllowsScreenAABB()
        {
            return kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.ScreenAABBOnly ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FullNativeChain;
        }

        static bool NativeLightListDiagnosticAllowsBigTile()
        {
            return kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FullNativeChain;
        }

        static bool NativeLightListDiagnosticAllowsFptl()
        {
            return kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlWithStockBounds ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlAndClusteredWithStockBounds ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlAndClusteredSkipStockVoxel ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FullNativeChain;
        }

        static bool NativeLightListDiagnosticAllowsMaterialIndirect()
        {
            return kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlWithStockBounds ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlAndClusteredWithStockBounds ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlAndClusteredSkipStockVoxel ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.MaterialIndirectWithStockFptl ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FullNativeChain;
        }

        static bool NativeLightListDiagnosticAllowsStandaloneMaterialIndirect()
        {
            return kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.MaterialIndirectWithStockFptl ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FullNativeChain;
        }

        static bool NativeLightListDiagnosticAllowsAnyIndirect()
        {
            return NativeLightListDiagnosticAllowsFptl() ||
                NativeLightListDiagnosticAllowsMaterialIndirect() ||
                NativeLightListDiagnosticAllowsClusteredOverwrite();
        }

        static bool NativeLightListDiagnosticAllowsClusteredOverwrite()
        {
            return kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlAndClusteredWithStockBounds ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlAndClusteredSkipStockVoxel ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FullNativeChain;
        }

        static bool NativeLightListDiagnosticSkipsStockVoxel()
        {
            return kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlAndClusteredSkipStockVoxel ||
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FullNativeChain;
        }

        static bool NativeLightListFptlParityProbeEnabled(bool nativeFptl, bool runFptl, bool projectionSupported, int totalLightCount, BufferHandle lightList, BufferHandle tileFeatureFlags, TextureHandle depthTexture, BufferHandle aabbBounds, BufferHandle convexBounds, BufferHandle lightVolumeData)
        {
            return kNativeLightListFptlParityProbeEnabled &&
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.MaterialIndirectWithStockFptl &&
                !nativeFptl &&
                runFptl &&
                projectionSupported &&
                totalLightCount > 0 &&
                lightList.IsValid() &&
                tileFeatureFlags.IsValid() &&
                depthTexture.IsValid() &&
                aabbBounds.IsValid() &&
                convexBounds.IsValid() &&
                lightVolumeData.IsValid() &&
                NativeLightListIndirectReady();
        }

        static uint[] CopyNativeLightListParityReadback(AsyncGPUReadbackRequest request, int compareDwords)
        {
            var data = request.GetData<uint>();
            int count = Mathf.Min(compareDwords, data.Length);
            var copy = new uint[count];
            for (int i = 0; i < count; ++i)
                copy[i] = data[i];
            return copy;
        }

        static int NativeLightListFptlTileBaseDword(int eye, int category, int tile, int numTiles)
        {
            int tileOffset = eye * (int)LightCategory.Count * numTiles + category * numTiles + tile;
            return tileOffset * InternalLightCullingDefs.s_LightDwordPerFptlTile;
        }

        static int NativeLightListFptlDwordInTileForLightOrdinal(int lightOrdinal)
        {
            return lightOrdinal == 0 ? 0 : 1 + ((lightOrdinal - 1) >> 1);
        }

        static uint NativeLightListFptlPackedLight(uint[] lightList, int baseDword, int lightOrdinal)
        {
            if (lightOrdinal == 0)
                return (lightList[baseDword] >> 16) & 0xffffu;

            uint packed = lightList[baseDword + NativeLightListFptlDwordInTileForLightOrdinal(lightOrdinal)];
            return (lightOrdinal & 1) != 0 ? packed & 0xffffu : (packed >> 16) & 0xffffu;
        }

        static bool TryFindNativeLightListFptlParityMismatch(NativeLightListFptlParitySample sample, int compareCount, out NativeLightListFptlParityMismatch mismatch)
        {
            mismatch = default;

            int dwordsPerTile = InternalLightCullingDefs.s_LightDwordPerFptlTile;
            int numTiles = Mathf.Max(0, sample.numTiles);
            int numTilesX = Mathf.Max(1, sample.numTilesX);
            int viewCount = Mathf.Max(1, sample.viewCount);
            int categoryCount = (int)LightCategory.Count;
            int maxPackedLights = dwordsPerTile * 2 - 1;

            for (int eye = 0; eye < viewCount; ++eye)
            {
                for (int category = 0; category < categoryCount; ++category)
                {
                    for (int tile = 0; tile < numTiles; ++tile)
                    {
                        int baseDword = NativeLightListFptlTileBaseDword(eye, category, tile, numTiles);
                        if (baseDword + dwordsPerTile > compareCount)
                        {
                            mismatch.reason = "readback-short";
                            mismatch.dword = baseDword;
                            mismatch.eye = eye;
                            mismatch.category = category;
                            mismatch.tile = tile;
                            mismatch.tileX = tile % numTilesX;
                            mismatch.tileY = tile / numTilesX;
                            return true;
                        }

                        uint stockHeader = sample.stock[baseDword];
                        uint nativeHeader = sample.native[baseDword];
                        int stockCount = (int)(stockHeader & 0xffffu);
                        int nativeCount = (int)(nativeHeader & 0xffffu);

                        if (stockCount != nativeCount)
                        {
                            mismatch.reason = "count";
                            mismatch.dword = baseDword;
                            mismatch.dwordInTile = 0;
                            mismatch.eye = eye;
                            mismatch.category = category;
                            mismatch.tile = tile;
                            mismatch.tileX = tile % numTilesX;
                            mismatch.tileY = tile / numTilesX;
                            mismatch.stockCount = stockCount;
                            mismatch.nativeCount = nativeCount;
                            mismatch.stockValue = (uint)stockCount;
                            mismatch.nativeValue = (uint)nativeCount;
                            mismatch.stockRaw = stockHeader;
                            mismatch.nativeRaw = nativeHeader;
                            return true;
                        }

                        if (stockCount > maxPackedLights)
                        {
                            mismatch.reason = "count-capacity";
                            mismatch.dword = baseDword;
                            mismatch.dwordInTile = 0;
                            mismatch.eye = eye;
                            mismatch.category = category;
                            mismatch.tile = tile;
                            mismatch.tileX = tile % numTilesX;
                            mismatch.tileY = tile / numTilesX;
                            mismatch.stockCount = stockCount;
                            mismatch.nativeCount = nativeCount;
                            mismatch.stockValue = (uint)stockCount;
                            mismatch.nativeValue = (uint)nativeCount;
                            mismatch.stockRaw = stockHeader;
                            mismatch.nativeRaw = nativeHeader;
                            return true;
                        }

                        for (int lightOrdinal = 0; lightOrdinal < stockCount; ++lightOrdinal)
                        {
                            uint stockLight = NativeLightListFptlPackedLight(sample.stock, baseDword, lightOrdinal);
                            uint nativeLight = NativeLightListFptlPackedLight(sample.native, baseDword, lightOrdinal);
                            if (stockLight == nativeLight)
                                continue;

                            int dwordInTile = NativeLightListFptlDwordInTileForLightOrdinal(lightOrdinal);
                            mismatch.reason = "light-index";
                            mismatch.dword = baseDword + dwordInTile;
                            mismatch.dwordInTile = dwordInTile;
                            mismatch.eye = eye;
                            mismatch.category = category;
                            mismatch.tile = tile;
                            mismatch.tileX = tile % numTilesX;
                            mismatch.tileY = tile / numTilesX;
                            mismatch.lightOrdinal = lightOrdinal;
                            mismatch.stockCount = stockCount;
                            mismatch.nativeCount = nativeCount;
                            mismatch.stockValue = stockLight;
                            mismatch.nativeValue = nativeLight;
                            mismatch.stockRaw = sample.stock[baseDword + dwordInTile];
                            mismatch.nativeRaw = sample.native[baseDword + dwordInTile];
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        static void CompleteNativeLightListFptlParitySample(uint frameIndex, NativeLightListFptlParitySample sample)
        {
            int compareCount = Mathf.Min(sample.compareDwords, Mathf.Min(sample.stock.Length, sample.native.Length));
            if (!TryFindNativeLightListFptlParityMismatch(sample, compareCount, out var mismatch))
            {
                int matches = ++s_NativeLightListFptlParityMatches;
                if (matches == 1 || (matches % 10) == 0)
                {
                    Debug.Log($"[FSR3DeckNativeLightListFPTLParity] semantic match frame={frameIndex} samples={matches} dwords={compareCount} tiles={sample.numTiles} lights={sample.totalLightCount}");
                }
                return;
            }

            ++s_NativeLightListFptlParityMismatches;
            Debug.LogWarning(
                $"[FSR3DeckNativeLightListFPTLParity] semantic mismatch frame={frameIndex} mismatchCount={s_NativeLightListFptlParityMismatches} reason={mismatch.reason} dword={mismatch.dword}/{compareCount} eye={mismatch.eye}/{sample.viewCount} category={mismatch.category} tile={mismatch.tile} tileXY=({mismatch.tileX},{mismatch.tileY}) tileDword={mismatch.dwordInTile} lightOrdinal={mismatch.lightOrdinal} stockCount={mismatch.stockCount} nativeCount={mismatch.nativeCount} stockValue={mismatch.stockValue} nativeValue={mismatch.nativeValue} stockRaw=0x{mismatch.stockRaw:X8} nativeRaw=0x{mismatch.nativeRaw:X8} lights={sample.totalLightCount}");
        }

        static void OnNativeLightListFptlParityReadback(uint frameIndex, bool nativeBuffer, int compareDwords, int numTiles, int numTilesX, int viewCount, int totalLightCount, AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                Debug.LogWarning($"[FSR3DeckNativeLightListFPTLParity] readback failed frame={frameIndex} buffer={(nativeBuffer ? "native" : "stock")}");
                return;
            }

            var copy = CopyNativeLightListParityReadback(request, compareDwords);
            NativeLightListFptlParitySample ready = null;
            lock (s_NativeLightListFptlParityLock)
            {
                if (!s_NativeLightListFptlParitySamples.TryGetValue(frameIndex, out var sample))
                {
                    sample = new NativeLightListFptlParitySample
                    {
                        compareDwords = compareDwords,
                        numTiles = numTiles,
                        numTilesX = numTilesX,
                        viewCount = viewCount,
                        totalLightCount = totalLightCount
                    };
                    s_NativeLightListFptlParitySamples.Add(frameIndex, sample);
                }

                if (nativeBuffer)
                    sample.native = copy;
                else
                    sample.stock = copy;

                if (sample.stock != null && sample.native != null)
                {
                    ready = sample;
                    s_NativeLightListFptlParitySamples.Remove(frameIndex);
                }
            }

            if (ready != null)
                CompleteNativeLightListFptlParitySample(frameIndex, ready);
        }

        static bool NativeLightListClusteredParityProbeEnabled(bool projectionSupported, bool runLightList, bool clusterNeedsDepth, int totalLightCount, BufferHandle perVoxelOffset, BufferHandle perVoxelLightLists, TextureHandle depthTexture, BufferHandle aabbBounds, BufferHandle convexBounds, BufferHandle lightVolumeData, BufferHandle bigTileLightList, bool useBigTile)
        {
            return kNativeLightListClusteredParityProbeEnabled &&
                kNativeLightListDiagnosticMode == NativeLightListDiagnosticMode.FptlWithStockBounds &&
                runLightList &&
                projectionSupported &&
                totalLightCount > 0 &&
                perVoxelOffset.IsValid() &&
                perVoxelLightLists.IsValid() &&
                (!clusterNeedsDepth || depthTexture.IsValid()) &&
                aabbBounds.IsValid() &&
                convexBounds.IsValid() &&
                lightVolumeData.IsValid() &&
                (!useBigTile || bigTileLightList.IsValid()) &&
                NativeLightListIndirectReady();
        }

        static void DecodeNativeLightListClusteredOffset(uint packed, out int start, out int count)
        {
            start = (int)(packed & 0x03ffffffu);
            count = (int)(packed >> 26);
        }

        static int NativeLightListClusteredOffsetIndex(int eye, int category, int cluster, int tile, int numTiles, int numClusters)
        {
            return (((eye * (int)LightCategory.Count + category) * numClusters + cluster) * numTiles) + tile;
        }

        static bool NativeLightListClusteredContains(uint[] list, int start, int count, uint value)
        {
            for (int i = 0; i < count; ++i)
            {
                if (list[start + i] == value)
                    return true;
            }
            return false;
        }

        static bool TryFindNativeLightListClusteredParityMismatch(NativeLightListClusteredParitySample sample, out NativeLightListClusteredParityMismatch mismatch)
        {
            mismatch = default;

            int numTiles = Mathf.Max(0, sample.numTiles);
            int numTilesX = Mathf.Max(1, sample.numTilesX);
            int viewCount = Mathf.Max(1, sample.viewCount);
            int numClusters = Mathf.Max(1, sample.numClusters);
            int categoryCount = (int)LightCategory.Count;
            int offsetCount = Mathf.Min(sample.offsetDwords, Mathf.Min(sample.stockOffset.Length, sample.nativeOffset.Length));
            int stockListCount = Mathf.Min(sample.listDwords, sample.stockList.Length);
            int nativeListCount = Mathf.Min(sample.listDwords, sample.nativeList.Length);

            for (int eye = 0; eye < viewCount; ++eye)
            {
                for (int category = 0; category < categoryCount; ++category)
                {
                    for (int cluster = 0; cluster < numClusters; ++cluster)
                    {
                        for (int tile = 0; tile < numTiles; ++tile)
                        {
                            int offsetIndex = NativeLightListClusteredOffsetIndex(eye, category, cluster, tile, numTiles, numClusters);
                            if (offsetIndex >= offsetCount)
                            {
                                mismatch.reason = "offset-readback-short";
                                mismatch.offsetIndex = offsetIndex;
                                mismatch.eye = eye;
                                mismatch.category = category;
                                mismatch.cluster = cluster;
                                mismatch.tile = tile;
                                mismatch.tileX = tile % numTilesX;
                                mismatch.tileY = tile / numTilesX;
                                return true;
                            }

                            uint stockRaw = sample.stockOffset[offsetIndex];
                            uint nativeRaw = sample.nativeOffset[offsetIndex];
                            DecodeNativeLightListClusteredOffset(stockRaw, out int stockStart, out int stockCount);
                            DecodeNativeLightListClusteredOffset(nativeRaw, out int nativeStart, out int nativeCount);

                            if (stockCount != nativeCount)
                            {
                                mismatch.reason = "count";
                                mismatch.offsetIndex = offsetIndex;
                                mismatch.eye = eye;
                                mismatch.category = category;
                                mismatch.cluster = cluster;
                                mismatch.tile = tile;
                                mismatch.tileX = tile % numTilesX;
                                mismatch.tileY = tile / numTilesX;
                                mismatch.stockCount = stockCount;
                                mismatch.nativeCount = nativeCount;
                                mismatch.stockStart = stockStart;
                                mismatch.nativeStart = nativeStart;
                                mismatch.stockValue = (uint)stockCount;
                                mismatch.nativeValue = (uint)nativeCount;
                                mismatch.stockRaw = stockRaw;
                                mismatch.nativeRaw = nativeRaw;
                                return true;
                            }

                            if (stockStart < 0 || stockStart + stockCount > stockListCount ||
                                nativeStart < 0 || nativeStart + nativeCount > nativeListCount)
                            {
                                mismatch.reason = "list-range";
                                mismatch.offsetIndex = offsetIndex;
                                mismatch.eye = eye;
                                mismatch.category = category;
                                mismatch.cluster = cluster;
                                mismatch.tile = tile;
                                mismatch.tileX = tile % numTilesX;
                                mismatch.tileY = tile / numTilesX;
                                mismatch.stockCount = stockCount;
                                mismatch.nativeCount = nativeCount;
                                mismatch.stockStart = stockStart;
                                mismatch.nativeStart = nativeStart;
                                mismatch.stockRaw = stockRaw;
                                mismatch.nativeRaw = nativeRaw;
                                return true;
                            }

                            for (int i = 0; i < stockCount; ++i)
                            {
                                uint stockLight = sample.stockList[stockStart + i];
                                if (NativeLightListClusteredContains(sample.nativeList, nativeStart, nativeCount, stockLight))
                                    continue;

                                mismatch.reason = "light-membership";
                                mismatch.offsetIndex = offsetIndex;
                                mismatch.eye = eye;
                                mismatch.category = category;
                                mismatch.cluster = cluster;
                                mismatch.tile = tile;
                                mismatch.tileX = tile % numTilesX;
                                mismatch.tileY = tile / numTilesX;
                                mismatch.lightOrdinal = i;
                                mismatch.stockCount = stockCount;
                                mismatch.nativeCount = nativeCount;
                                mismatch.stockStart = stockStart;
                                mismatch.nativeStart = nativeStart;
                                mismatch.stockValue = stockLight;
                                mismatch.nativeValue = nativeCount > 0 ? sample.nativeList[nativeStart] : 0u;
                                mismatch.stockRaw = stockRaw;
                                mismatch.nativeRaw = nativeRaw;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        static void CompleteNativeLightListClusteredParitySample(uint frameIndex, NativeLightListClusteredParitySample sample)
        {
            if (!TryFindNativeLightListClusteredParityMismatch(sample, out var mismatch))
            {
                int matches = ++s_NativeLightListClusteredParityMatches;
                if (matches == 1 || (matches % 10) == 0)
                {
                    Debug.Log($"[FSR3DeckNativeLightListClusteredParity] semantic match frame={frameIndex} samples={matches} offsetDwords={sample.offsetDwords} listDwords={sample.listDwords} tiles={sample.numTiles} clusters={sample.numClusters} lights={sample.totalLightCount}");
                }
                return;
            }

            ++s_NativeLightListClusteredParityMismatches;
            Debug.LogWarning(
                $"[FSR3DeckNativeLightListClusteredParity] semantic mismatch frame={frameIndex} mismatchCount={s_NativeLightListClusteredParityMismatches} reason={mismatch.reason} offset={mismatch.offsetIndex}/{sample.offsetDwords} eye={mismatch.eye}/{sample.viewCount} category={mismatch.category} cluster={mismatch.cluster}/{sample.numClusters} tile={mismatch.tile} tileXY=({mismatch.tileX},{mismatch.tileY}) lightOrdinal={mismatch.lightOrdinal} stockCount={mismatch.stockCount} nativeCount={mismatch.nativeCount} stockStart={mismatch.stockStart} nativeStart={mismatch.nativeStart} stockValue={mismatch.stockValue} nativeValue={mismatch.nativeValue} stockRaw=0x{mismatch.stockRaw:X8} nativeRaw=0x{mismatch.nativeRaw:X8} lights={sample.totalLightCount}");
        }

        static void OnNativeLightListClusteredParityReadback(uint frameIndex, int bufferKind, int offsetDwords, int listDwords, int numTiles, int numTilesX, int numTilesY, int viewCount, int numClusters, int totalLightCount, AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                Debug.LogWarning($"[FSR3DeckNativeLightListClusteredParity] readback failed frame={frameIndex} bufferKind={bufferKind}");
                return;
            }

            int compareDwords = bufferKind <= 1 ? offsetDwords : listDwords;
            var copy = CopyNativeLightListParityReadback(request, compareDwords);
            NativeLightListClusteredParitySample ready = null;
            lock (s_NativeLightListClusteredParityLock)
            {
                if (!s_NativeLightListClusteredParitySamples.TryGetValue(frameIndex, out var sample))
                {
                    sample = new NativeLightListClusteredParitySample
                    {
                        offsetDwords = offsetDwords,
                        listDwords = listDwords,
                        numTiles = numTiles,
                        numTilesX = numTilesX,
                        numTilesY = numTilesY,
                        viewCount = viewCount,
                        numClusters = numClusters,
                        totalLightCount = totalLightCount
                    };
                    s_NativeLightListClusteredParitySamples.Add(frameIndex, sample);
                }

                switch (bufferKind)
                {
                    case 0: sample.stockOffset = copy; break;
                    case 1: sample.nativeOffset = copy; break;
                    case 2: sample.stockList = copy; break;
                    case 3: sample.nativeList = copy; break;
                }

                if (sample.stockOffset != null && sample.nativeOffset != null &&
                    sample.stockList != null && sample.nativeList != null)
                {
                    ready = sample;
                    s_NativeLightListClusteredParitySamples.Remove(frameIndex);
                }
            }

            if (ready != null)
                CompleteNativeLightListClusteredParitySample(frameIndex, ready);
        }

        static string NativeLightListDiagnosticDescription()
        {
            switch (kNativeLightListDiagnosticMode)
            {
                case NativeLightListDiagnosticMode.VisualParityHold:
                    return "visual parity hold: stock HDRP owns ScreenAABB/BigTile/FPTL/material/indirect";
                case NativeLightListDiagnosticMode.ScreenAABBOnly:
                    return "ScreenAABB-only: native scrbound prepass, stock BigTile/FPTL/material/indirect";
                case NativeLightListDiagnosticMode.FptlWithStockBounds:
                    return "FPTL with stock bounds: stock ScreenAABB/BigTile, native FPTL plus required material/indirect rebuild";
                case NativeLightListDiagnosticMode.FptlAndClusteredWithStockBounds:
                    return "FPTL + clustered with stock bounds: stock ScreenAABB/BigTile, native FPTL/material/indirect, native clustered visible overwrite";
                case NativeLightListDiagnosticMode.FptlAndClusteredSkipStockVoxel:
                    return "FPTL + clustered performance-live with stock bounds: stock ScreenAABB/BigTile, native FPTL/material/indirect, native clustered output, stock VoxelLightListGeneration skipped";
                case NativeLightListDiagnosticMode.MaterialIndirectWithStockFptl:
                    return "material/indirect only: stock ScreenAABB/BigTile/FPTL, native material flags and dispatch-indirect build";
                case NativeLightListDiagnosticMode.FullNativeChain:
                    return "full native chain: native ScreenAABB/BigTile/FPTL/material/indirect where gates permit";
                default:
                    return "unknown native light-list diagnostic mode";
            }
        }

        static IntPtr GetNativeLightListPrePassDispatchCallback()
        {
            if (!NativeLightListIndirectRuntimeSupported())
                return IntPtr.Zero;
            if (!NativeLightListParamsAbiSupported())
                return IntPtr.Zero;

            if (!s_NativeLightListPrePassProbeAttempted)
            {
                s_NativeLightListPrePassProbeAttempted = true;
                try
                {
                    s_NativeLightListPrePassCallback = FSR3_NativeLightListPrePass_GetDispatchCallback();
                    s_NativeLightListPrePassWaitCallback = FSR3_NativeLightListPrePass_GetWaitCallback();
                }
                catch (DllNotFoundException)
                {
                    s_NativeLightListPrePassCallback = IntPtr.Zero;
                    s_NativeLightListPrePassWaitCallback = IntPtr.Zero;
                }
                catch (EntryPointNotFoundException)
                {
                    s_NativeLightListPrePassCallback = IntPtr.Zero;
                    s_NativeLightListPrePassWaitCallback = IntPtr.Zero;
                }
            }

            return s_NativeLightListPrePassCallback;
        }

        static bool NativeLightListPrePassReady()
        {
            bool ready = GetNativeLightListPrePassDispatchCallback() != IntPtr.Zero &&
                s_NativeLightListPrePassWaitCallback != IntPtr.Zero;
            if (ready && !s_NativeLightListPrePassReadyLogged)
            {
                s_NativeLightListPrePassReadyLogged = true;
                Debug.Log("[FSR3DeckNativeLightList] Native PrePass callbacks ready.");
            }

            return ready;
        }

        static bool NativeLightListClusteredCandidate(BuildGPULightListPassData data)
        {
            return data.runLightList &&
                data.nativeFptlProjectionSupported &&
                data.totalLightCount > 0 &&
                data.output.perVoxelOffset.IsValid() &&
                data.output.perVoxelLightLists.IsValid() &&
                (!data.clusterNeedsDepth ||
                 (data.depthBuffer.IsValid() && data.output.perTileLogBaseTweak.IsValid())) &&
                data.AABBBoundsBuffer.IsValid() &&
                data.convexBoundsBuffer.IsValid() &&
                data.lightVolumeDataBuffer.IsValid() &&
                (!data.runBigTilePrepass || data.output.bigTileLightList.IsValid()) &&
                NativeLightListIndirectReady();
        }

        static bool NativeLightListIndirectEnabled(BuildGPULightListPassData data)
        {
            if (NativeLightListVisualParityHold())
                return false;

            bool nativeFptlCandidate =
                data.runLightList &&
                data.runFPTL &&
                data.nativeFptlProjectionSupported &&
                data.output.lightList.IsValid() &&
                data.AABBBoundsBuffer.IsValid() &&
                data.convexBoundsBuffer.IsValid() &&
                data.lightVolumeDataBuffer.IsValid() &&
                data.depthBuffer.IsValid() &&
                (!data.runBigTilePrepass || data.output.bigTileLightList.IsValid());
            bool featureVariantBuffersOk =
                !data.enableFeatureVariants ||
                (data.output.tileList.IsValid() &&
                 data.output.dispatchIndirectBuffer.IsValid());
            bool wantsNativeFptl = NativeLightListDiagnosticAllowsFptl() && nativeFptlCandidate;
            bool wantsNativeMaterialIndirect = data.enableFeatureVariants &&
                (NativeLightListDiagnosticAllowsStandaloneMaterialIndirect() ||
                 (NativeLightListDiagnosticAllowsFptl() &&
                  nativeFptlCandidate));
            bool wantsNativeClustered =
                NativeLightListDiagnosticAllowsClusteredOverwrite() &&
                NativeLightListClusteredCandidate(data);

            return (wantsNativeMaterialIndirect || wantsNativeFptl || wantsNativeClustered) &&
                data.output.tileFeatureFlags.IsValid() &&
                featureVariantBuffersOk &&
                NativeLightListIndirectReady();
        }

        static uint NativeLightListFptlBaseFeatureFlags(BuildGPULightListPassData data)
        {
            uint baseFeatureFlags = 0;
            if (data.directionalLightCount > 0)
                baseFeatureFlags |= (uint)LightFeatureFlags.Directional;
            if (data.skyEnabled)
                baseFeatureFlags |= (uint)LightFeatureFlags.Sky;
            if (!data.computeMaterialVariants)
                baseFeatureFlags |= LightDefinitions.s_MaterialFeatureMaskFlags;
            return baseFeatureFlags;
        }

        static bool NativeLightListFptlEnabled(BuildGPULightListPassData data)
        {
            return data.nativeIndirect &&
                NativeLightListDiagnosticAllowsFptl() &&
                data.runLightList &&
                data.runFPTL &&
                data.nativeFptlProjectionSupported &&
                data.output.lightList.IsValid() &&
                data.output.tileFeatureFlags.IsValid() &&
                data.AABBBoundsBuffer.IsValid() &&
                data.convexBoundsBuffer.IsValid() &&
                data.lightVolumeDataBuffer.IsValid() &&
                data.depthBuffer.IsValid() &&
                (!data.runBigTilePrepass || data.output.bigTileLightList.IsValid());
        }

        static bool NativeLightListClusteredOverwriteEnabled(BuildGPULightListPassData data)
        {
            return data.nativeIndirect &&
                NativeLightListDiagnosticAllowsClusteredOverwrite() &&
                NativeLightListClusteredCandidate(data);
        }

        // Native BigTile gate (audit follow-up 2026-05-12).
        //
        // KNOWN-BLOCKED 2026-05-12: returning false. Native BigTile is
        // implemented end-to-end (shader + plugin + render-graph wiring) but
        // cannot activate yet because the chain has a downstream reader we
        // hadn't accounted for. Inside the same BuildGPULightList
        // AddComputePass that contains BigTilePrepass, VoxelLightList
        // Generation also runs (line 366+ in this file). Voxel reads
        // g_vBigTileLightList as an input. With native BigTile on, the
        // HDRP-stock BigTile dispatch is skipped, but our native BigTile
        // runs in a SEPARATE AddUnsafePass (FSR3 Native LightList Indirect)
        // that the render-graph orders AFTER BuildGPULightList — so Voxel
        // would read uninitialized / stale bigTileLightList contents and
        // silently corrupt cluster light lists. Cluster lighting drives
        // transparent-pass lighting + volumetrics, so the symptom is
        // wrong transparent-object shading (often visible in motion).
        //
        // The same ordering issue applies to a future native scrbound port
        // (Voxel also reads g_vBoundsBuffer = scrbound output).
        //
        // Unblock path: port VoxelLightListGeneration native too (next
        // pass-replacement target), then flip this gate to the full
        // predicate below. The native plugin's NativeLightListBigTile
        // pipeline + dispatch + timestamp infrastructure is in place and
        // will activate as soon as this returns true.
        //
        // History 2026-05-12: previously forced off to isolate a visual-skew
        // bug observed when both ScreenAABB and BigTile native dispatched
        // together. Side-by-side audit of NativeLightListBigTile.comp.glsl vs
        // HDRP's lightlistbuild-bigtile.compute found no functional divergence
        // (light bound 14-float Exact packing matches, projection matrix
        // extraction matches, bitonic sort matches, output dword pack matches,
        // volumetric pack header convention matches). The audit-fix that
        // aligned `nativeBigTileHint` with the then-current final-gate
        // predicate (removed a stale `!earlySupportsVolumetric` check)
        // was the most likely root cause of the skew — render-graph saw a
        // Write→Write dependency on bigTileLightList because the hint said
        // "stock writes" while the final gate said "native writes", letting
        // the graph order PrePass after AddComputePass and feeding empty
        // bigTile data to FPTL/Voxel. That finding is currently superseded
        // by NativeLightListVisualParityHold(), which keeps all native
        // light-list replacements off while we re-establish visual parity.
        static bool NativeLightListBigTileEnabled(BuildGPULightListPassData data)
        {
            if (!NativeLightListDiagnosticAllowsBigTile())
                return false;

            // 2026-05-13 visual-quality guard: when HDRP allocates the
            // volumetric BigTile list, stock volumetric lighting consumes
            // that buffer directly. The remaining reported artifact is
            // bright/blocky light glow, so keep HDRP's stock BigTile kernel
            // responsible for both regular + volumetric BigTile lists in
            // volumetric-capable assets until the native volumetric pack has
            // a capture-proven parity check. The broader visual-parity hold
            // above may disable this path before this function is reached.
            if (data.supportsVolumetric)
                return false;

            if (!(data.runLightList &&
                  data.runBigTilePrepass && data.nativeFptlProjectionSupported &&
                  data.output.bigTileLightList.IsValid() &&
                  data.AABBBoundsBuffer.IsValid() &&
                  data.convexBoundsBuffer.IsValid() &&
                  data.lightVolumeDataBuffer.IsValid() &&
                  NativeLightListPrePassReady()))
                return false;
            return true;
        }

        // ScreenAABB (scrbound) gate (2026-05-12). Activates when the native
        // PrePass infrastructure is ready and the projection-matrix push
        // assumptions hold. Single-view only (Deck has viewCount=1).
        static bool NativeLightListScreenAABBEnabled(BuildGPULightListPassData data)
        {
            return NativeLightListDiagnosticAllowsScreenAABB() &&
                data.runLightList &&
                data.nativeFptlProjectionSupported &&
                data.AABBBoundsBuffer.IsValid() &&
                data.convexBoundsBuffer.IsValid() &&
                NativeLightListPrePassReady();
        }

        static bool NativeLightListTileFlagsWritten(BuildGPULightListPassData data)
        {
            return data.runLightList && data.runFPTL && data.enableFeatureVariants;
        }

        static uint NativeLightListMaterialBaseFeatureFlags(BuildGPULightListPassData data, bool tileFlagsWritten)
        {
            uint baseFeatureFlags = 0;
            if (!data.computeLightVariants)
                baseFeatureFlags |= LightDefinitions.s_LightFeatureMaskFlags;

            if (!tileFlagsWritten)
            {
                if (data.directionalLightCount > 0)
                    baseFeatureFlags |= (uint)LightFeatureFlags.Directional;
                if (data.skyEnabled)
                    baseFeatureFlags |= (uint)LightFeatureFlags.Sky;
                if (!data.computeMaterialVariants)
                    baseFeatureFlags |= LightDefinitions.s_MaterialFeatureMaskFlags;
            }

            return baseFeatureFlags;
        }

        static uint NativeLightListMaterialFlagsMode(BuildGPULightListPassData data, bool tileFlagsWritten)
        {
            if (!data.enableFeatureVariants)
                return 0;

            bool needModifyingTileFeatures = !tileFlagsWritten || data.computeMaterialVariants;
            if (!needModifyingTileFeatures)
                return 0;

            return tileFlagsWritten && data.computeLightVariants ? 2u : 1u;
        }

        static bool NativeLightListMaterialFlagsEnabled(BuildGPULightListPassData data, bool tileFlagsWritten)
        {
            uint mode = NativeLightListMaterialFlagsMode(data, tileFlagsWritten);
            return mode != 0 &&
                data.stencilTexture.IsValid() &&
                data.gBuffer[2].IsValid();
        }

        unsafe void PrepareBuildGPULightListPassData(
            RenderGraph renderGraph,
            IComputeRenderGraphBuilder builder,
            HDCamera hdCamera,
            TileAndClusterData tileAndClusterData,
            ref ShaderVariablesLightList constantBuffer,
            int totalLightCount,
            TextureHandle depthStencilBuffer,
            TextureHandle stencilBufferCopy,
            GBufferOutput gBuffer,
            BuildGPULightListPassData passData,
            // 2026-05-12: precomputed native-pass gates so this function can
            // declare conditional UseBuffer access flags. When the PrePass is
            // active and writes AABBBoundsBuffer / bigTileLightList, this
            // pass declares Read on those instead of ReadWrite/Write — that's
            // what lets render-graph order the PrePass first via the Write→Read
            // dependency.
            bool nativeScreenAABBHint,
            bool nativeBigTileHint)
        {
            var camera = hdCamera.camera;

            var w = (int)hdCamera.screenSize.x;
            var h = (int)hdCamera.screenSize.y;

            // Fill the shared constant buffer.
            // We don't fill directly the one in the parameter struct because we will need those parameters for volumetric lighting as well.
            ref var cb = ref constantBuffer;
            var temp = new Matrix4x4();
            temp.SetRow(0, new Vector4(0.5f * w, 0.0f, 0.0f, 0.5f * w));
            temp.SetRow(1, new Vector4(0.0f, 0.5f * h, 0.0f, 0.5f * h));
            temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
            temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

            // camera to screen matrix (and it's inverse)
            for (int viewIndex = 0; viewIndex < hdCamera.viewCount; ++viewIndex)
            {
                var proj = hdCamera.xr.enabled ? hdCamera.xr.GetProjMatrix(viewIndex) : camera.projectionMatrix;
                // Note: we need to take into account the TAA jitter when indexing the light list
                proj = hdCamera.RequiresCameraJitter() ? hdCamera.GetJitteredProjectionMatrix(proj) : proj;

                m_LightListProjMatrices[viewIndex] = proj * s_FlipMatrixLHSRHS;

                var tempMatrix = temp * m_LightListProjMatrices[viewIndex];
                var invTempMatrix = tempMatrix.inverse;
                if (viewIndex == 0)
                {
                    passData.nativeFptlScrSx = tempMatrix.m00;
                    passData.nativeFptlScrSy = tempMatrix.m11;
                    passData.nativeFptlScrCx = tempMatrix.m02;
                    passData.nativeFptlScrCy = tempMatrix.m12;
                    passData.nativeFptlScrCxOrtho = tempMatrix.m03;
                    passData.nativeFptlScrCyOrtho = tempMatrix.m13;
                    passData.nativeFptlInvM22 = invTempMatrix.m22;
                    passData.nativeFptlInvM23 = invTempMatrix.m23;
                    passData.nativeFptlInvM32 = invTempMatrix.m32;
                    passData.nativeFptlInvM33 = invTempMatrix.m33;
                }

                for (int i = 0; i < 16; ++i)
                {
                    cb.g_mScrProjectionArr[viewIndex * 16 + i] = tempMatrix[i];
                    cb.g_mInvScrProjectionArr[viewIndex * 16 + i] = invTempMatrix[i];
                }
            }

            // camera to screen matrix (and it's inverse)
            for (int viewIndex = 0; viewIndex < hdCamera.viewCount; ++viewIndex)
            {
                temp.SetRow(0, new Vector4(1.0f, 0.0f, 0.0f, 0.0f));
                temp.SetRow(1, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
                temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
                temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

                var tempMatrix = temp * m_LightListProjMatrices[viewIndex];
                var invTempMatrix = tempMatrix.inverse;

                for (int i = 0; i < 16; ++i)
                {
                    cb.g_mProjectionArr[viewIndex * 16 + i] = tempMatrix[i];
                    cb.g_mInvProjectionArr[viewIndex * 16 + i] = invTempMatrix[i];
                }
            }

            var decalDatasCount = Math.Min(DecalSystem.m_DecalDatasCount, m_MaxDecalsOnScreen);

            cb.g_iNrVisibLights = totalLightCount;
            cb.g_screenSize = hdCamera.screenSize; // TODO remove and use global one.
            cb.g_viDimensions = new Vector2Int((int)hdCamera.screenSize.x, (int)hdCamera.screenSize.y);
            cb.g_isOrthographic = camera.orthographic ? 1u : 0u;
            cb.g_BaseFeatureFlags = 0; // Filled for each individual pass.
            cb.g_iNumSamplesMSAA = (int)hdCamera.msaaSamples;
            cb._EnvLightIndexShift = (uint)m_GpuLightsBuilder.lightsCount;
            cb._DecalIndexShift = (uint)(m_GpuLightsBuilder.lightsCount + m_lightList.envLights.Count);

            // Copy the constant buffer into the parameter struct.
            passData.lightListCB = cb;

            passData.totalLightCount = totalLightCount;
            passData.runLightList = passData.totalLightCount > 0;
            passData.clearLightLists = false;

            // TODO RENDERGRAPH: This logic is flawed with Render Graph.
            // In theory buffers memory might be reused from another usage entirely so keeping track of its "cleared" state does not represent the truth of their content.
            // In practice though, when resolution stays the same, buffers will be the same reused from one frame to another
            // because for now buffers are pooled based on their passData. When we do proper aliasing though, we might end up with any random chunk of memory.

            if (!tileAndClusterData.listsAreInitialized)
            {
                // On some platforms initial values in buffer might not be zero but random data.
                // In the case of buffer "PerVoxelOffset" this can cause GPU to access incorrect memory locations and crash.
                // To avoid that we make sure that the buffers are cleared at least once before they start being used.
                passData.clearLightLists = true;
                tileAndClusterData.listsAreInitialized = true;
            }
			
            // Always build the light list in XR mode to avoid issues with multi-pass
            if (hdCamera.xr.enabled)
            {
                passData.runLightList = true;
            }
            else if (!passData.runLightList && !tileAndClusterData.listsAreClear)
            {
                passData.clearLightLists = true;
                // After that, No need to clear it anymore until we start and stop running light list building.
                tileAndClusterData.listsAreClear = true;
            }
            else if (passData.runLightList)
            {
                tileAndClusterData.listsAreClear = false;
            }

            passData.viewCount = hdCamera.viewCount;
            passData.enableFeatureVariants = GetFeatureVariantsEnabled(hdCamera.frameSettings) && tileAndClusterData.hasTileBuffers;
            passData.computeMaterialVariants = hdCamera.frameSettings.IsEnabled(FrameSettingsField.ComputeMaterialVariants);
            passData.computeLightVariants = hdCamera.frameSettings.IsEnabled(FrameSettingsField.ComputeLightVariants);
            passData.directionalLightCount = m_GpuLightsBuilder.directionalLightCount;
            passData.canClearLightList = m_GpuLightsBuilder != null && m_lightList != null;
            passData.skyEnabled = m_SkyManager.IsLightingSkyValid(hdCamera);

            bool isProjectionOblique = GeometryUtils.IsProjectionMatrixOblique(m_LightListProjMatrices[0]);
            passData.nativeFptlProjectionSupported = !isProjectionOblique && passData.viewCount == 1 && !hdCamera.msaaEnabled;

            // Clear light lsts
            passData.clearLightListCS = runtimeShaders.clearLightListsCS;
            passData.clearLightListKernel = passData.clearLightListCS.FindKernel("ClearList");

            // Screen space AABB
            passData.screenSpaceAABBShader = buildScreenAABBShader;
            passData.screenSpaceAABBKernel = 0;

            // Big tile prepass
            passData.runBigTilePrepass = hdCamera.frameSettings.IsEnabled(FrameSettingsField.BigTilePrepass);
            passData.bigTilePrepassShader = buildPerBigTileLightListShader;
            passData.bigTilePrepassKernel = s_GenListPerBigTileKernel;
            passData.numBigTilesX = (w + 63) / 64;
            passData.numBigTilesY = (h + 63) / 64;
            passData.supportsVolumetric = currentAsset.currentPlatformRenderPipelineSettings.supportVolumetrics;

            // Fptl
            passData.runFPTL = hdCamera.frameSettings.fptl && tileAndClusterData.hasTileBuffers;
            passData.buildPerTileLightListShader = buildPerTileLightListShader;
            passData.buildPerTileLightListShader.shaderKeywords = null;
            if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.BigTilePrepass))
            {
                passData.buildPerTileLightListShader.EnableKeyword("USE_TWO_PASS_TILED_LIGHTING");
            }
            if (isProjectionOblique)
            {
                passData.buildPerTileLightListShader.EnableKeyword("USE_OBLIQUE_MODE");
            }
            if (GetFeatureVariantsEnabled(hdCamera.frameSettings))
            {
                passData.buildPerTileLightListShader.EnableKeyword("USE_FEATURE_FLAGS");
            }
            passData.buildPerTileLightListKernel = s_GenListPerTileKernel;

            passData.numTilesFPTLX = GetNumTileFtplX(hdCamera);
            passData.numTilesFPTLY = GetNumTileFtplY(hdCamera);
            passData.numTilesFPTL = passData.numTilesFPTLX * passData.numTilesFPTLY;

            // Cluster
            bool msaa = hdCamera.msaaEnabled;
            var clustPrepassSourceIdx = hdCamera.frameSettings.IsEnabled(FrameSettingsField.BigTilePrepass) ? ClusterPrepassSource.BigTile : ClusterPrepassSource.None;
            var clustDepthSourceIdx = ClusterDepthSource.NoDepth;
            if (tileAndClusterData.clusterNeedsDepth)
                clustDepthSourceIdx = msaa ? ClusterDepthSource.MSAA_Depth : ClusterDepthSource.Depth;

            passData.buildPerVoxelLightListShader = buildPerVoxelLightListShader;
            passData.clearClusterAtomicIndexShader = clearClusterAtomicIndexShader;
            passData.buildPerVoxelLightListKernel = isProjectionOblique ? s_ClusterObliqueKernels[(int)clustPrepassSourceIdx, (int)clustDepthSourceIdx] : s_ClusterKernels[(int)clustPrepassSourceIdx, (int)clustDepthSourceIdx];
            passData.numTilesClusterX = GetNumTileClusteredX(hdCamera);
            passData.numTilesClusterY = GetNumTileClusteredY(hdCamera);
            passData.clusterNeedsDepth = tileAndClusterData.clusterNeedsDepth;

            // Build dispatch indirect
            passData.buildMaterialFlagsShader = buildMaterialFlagsShader;
            passData.clearDispatchIndirectShader = clearDispatchIndirectShader;
            passData.buildDispatchIndirectShader = buildDispatchIndirectShader;
            passData.buildDispatchIndirectShader.shaderKeywords = null;

            // Depending on frame setting configurations we might not have written to a depth buffer yet so when executing the pass it might not be valid.
            if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.OpaqueObjects))
            {
                passData.depthBuffer = depthStencilBuffer;
                passData.stencilTexture = stencilBufferCopy;
            }
            else
            {
                passData.depthBuffer = renderGraph.defaultResources.blackTextureXR;
                passData.stencilTexture = renderGraph.defaultResources.blackTextureXR;
            }
            builder.UseTexture(passData.depthBuffer, AccessFlags.Read);
            builder.UseTexture(passData.stencilTexture, AccessFlags.Read);

            if (passData.computeMaterialVariants && passData.enableFeatureVariants)
            {
                // When opaques are disabled, gbuffer count is zero.
                // Unfortunately, compute shader will then complains some textures aren't bound, so we need to use black textures instead.
                for (int i = 0; i < gBuffer.gBufferCount; ++i)
                {
                    passData.gBuffer[i] = gBuffer.mrt[i];
                    builder.UseTexture(passData.gBuffer[i], AccessFlags.Read);
                }
                for (int i = gBuffer.gBufferCount; i < 7; ++i) {
                    passData.gBuffer[i] = renderGraph.defaultResources.blackTextureXR;
                    builder.UseTexture(passData.gBuffer[i], AccessFlags.Read);
                }
                passData.gBufferCount = 7;
            }

            // Here we use m_MaxViewCount/m_MaxWidthHeight to avoid always allocating buffers of different sizes for each camera.
            // This way we'll be reusing them more often.

            // Those buffer are filled with the CPU outside of the render graph.
            passData.convexBoundsBuffer = renderGraph.ImportBuffer(tileAndClusterData.convexBoundsBuffer);
            builder.UseBuffer(passData.convexBoundsBuffer, AccessFlags.Read);
            passData.lightVolumeDataBuffer = renderGraph.ImportBuffer(tileAndClusterData.lightVolumeDataBuffer);
            builder.UseBuffer(passData.lightVolumeDataBuffer, AccessFlags.Read);

            passData.globalLightListAtomic = builder.CreateTransientBuffer(new BufferDesc(1, sizeof(uint)) { name = "LightListAtomic" });
            passData.AABBBoundsBuffer = renderGraph.CreateBuffer(new BufferDesc(m_MaxViewCount * 2 * tileAndClusterData.maxLightCount, 4 * sizeof(float)) { name = "AABBBoundBuffer" });
            // ReadWrite normally (HDRP-stock scrbound writes, downstream Voxel
            // reads). When nativeScreenAABBHint is on, the PrePass owns the
            // write and this pass only reads — declaring Read here lets
            // render-graph order PrePass first via the dependency.
            builder.UseBuffer(passData.AABBBoundsBuffer,
                nativeScreenAABBHint ? AccessFlags.Read : AccessFlags.ReadWrite);

            var nrTilesX = (m_MaxCameraWidth + LightDefinitions.s_TileSizeFptl - 1) / LightDefinitions.s_TileSizeFptl;
            var nrTilesY = (m_MaxCameraHeight + LightDefinitions.s_TileSizeFptl - 1) / LightDefinitions.s_TileSizeFptl;
            var nrTiles = nrTilesX * nrTilesY * m_MaxViewCount;

            if (tileAndClusterData.hasTileBuffers)
            {
                // note that nrTiles include the viewCount in allocation below
                // Tile buffers
                passData.output.lightList = renderGraph.CreateBuffer(new BufferDesc((int)LightCategory.Count * InternalLightCullingDefs.s_LightDwordPerFptlTile * nrTiles, sizeof(uint)) { name = "LightList" });
                builder.UseBuffer(passData.output.lightList, AccessFlags.Write);
                passData.output.tileList = renderGraph.CreateBuffer(new BufferDesc(LightDefinitions.s_NumFeatureVariants * nrTiles, sizeof(uint)) { name = "TileList" });
                builder.UseBuffer(passData.output.tileList, AccessFlags.Write);
                passData.output.tileFeatureFlags = renderGraph.CreateBuffer(new BufferDesc(nrTiles, sizeof(uint)) { name = "TileFeatureFlags" });
                builder.UseBuffer(passData.output.tileFeatureFlags, AccessFlags.Write);
                // DispatchIndirect: Buffer with arguments has to have three integer numbers at given argsOffset offset: number of work groups in X dimension, number of work groups in Y dimension, number of work groups in Z dimension.
                // DrawProceduralIndirect: Buffer with arguments has to have four integer numbers at given argsOffset offset: vertex count per instance, instance count, start vertex location, and start instance location
                // Use use max size of 4 unit for allocation
                passData.output.dispatchIndirectBuffer = renderGraph.CreateBuffer(new BufferDesc(m_MaxViewCount * LightDefinitions.s_NumFeatureVariants * 4, sizeof(uint), GraphicsBuffer.Target.IndirectArguments) { name = "DispatchIndirectBuffer" });
                builder.UseBuffer(passData.output.dispatchIndirectBuffer, AccessFlags.Write);
            }

            // Big Tile buffer
            if (passData.runBigTilePrepass)
            {
                var nrBigTilesX = (m_MaxCameraWidth + 63) / 64;
                var nrBigTilesY = (m_MaxCameraHeight + 63) / 64;
                var nrBigTiles = nrBigTilesX * nrBigTilesY * m_MaxViewCount;
                passData.output.bigTileLightList = renderGraph.CreateBuffer(new BufferDesc(InternalLightCullingDefs.s_MaxNrBigTileLightsPlusOne * nrBigTiles / 2, sizeof(uint)) { name = "BigTiles" });
                // Same conditional access pattern as AABBBoundsBuffer above:
                // Read when the PrePass owns the write.
                builder.UseBuffer(passData.output.bigTileLightList,
                    nativeBigTileHint ? AccessFlags.Read : AccessFlags.Write);
                if (passData.supportsVolumetric)
                {
                    passData.output.bigTileVolumetricLightList = renderGraph.CreateBuffer(new BufferDesc(InternalLightCullingDefs.s_MaxNrBigTileLightsPlusOne * nrBigTiles / 2, sizeof(uint)) { name = "BigTiles For Volumetric" });
                    // Same conditional access flag as bigTileLightList — Read
                    // when the PrePass owns the write, Write otherwise.
                    builder.UseBuffer(passData.output.bigTileVolumetricLightList,
                        nativeBigTileHint ? AccessFlags.Read : AccessFlags.Write);
                }
            }

            // Cluster buffers
            var nrClustersX = (m_MaxCameraWidth + LightDefinitions.s_TileSizeClustered - 1) / LightDefinitions.s_TileSizeClustered;
            var nrClustersY = (m_MaxCameraHeight + LightDefinitions.s_TileSizeClustered - 1) / LightDefinitions.s_TileSizeClustered;
            var nrClusterTiles = nrClustersX * nrClustersY * m_MaxViewCount;

            passData.output.perVoxelOffset = renderGraph.CreateBuffer(new BufferDesc((int)LightCategory.Count * (1 << k_Log2NumClusters) * nrClusterTiles, sizeof(uint)) { name = "PerVoxelOffset" });
            builder.UseBuffer(passData.output.perVoxelOffset, AccessFlags.Write);
            passData.output.perVoxelLightLists = renderGraph.CreateBuffer(new BufferDesc(NumLightIndicesPerClusteredTile() * nrClusterTiles, sizeof(uint)) { name = "PerVoxelLightList" });
            builder.UseBuffer(passData.output.perVoxelLightLists, AccessFlags.Write);
            if (tileAndClusterData.clusterNeedsDepth)
            {
                passData.output.perTileLogBaseTweak = renderGraph.CreateBuffer(new BufferDesc(nrClusterTiles, sizeof(float)) { name = "PerTileLogBaseTweak" });
                builder.UseBuffer(passData.output.perTileLogBaseTweak, AccessFlags.Write);
            }
        }

        BuildGPULightListOutput BuildGPULightList(
            RenderGraph renderGraph,
            HDCamera hdCamera,
            TileAndClusterData tileAndClusterData,
            int totalLightCount,
            ref ShaderVariablesLightList constantBuffer,
            TextureHandle depthStencilBuffer,
            TextureHandle stencilBufferCopy,
            GBufferOutput gBuffer)
        {
            BuildGPULightListOutput output;
            bool nativeIndirect;
            bool nativeFptl;
            bool nativeClustered;
            bool nativeBigTile;
            bool nativeScreenAABB;
            bool nativeMaterialFlags;
            bool nativeFptlProjectionSupported;
            bool useBigTileForFptl;
            BufferHandle aabbBounds;
            BufferHandle convexBounds;
            BufferHandle lightVolumeData;
            BufferHandle bigTileLightList;
            BufferHandle bigTileVolumetricLightListCapture;
            TextureHandle depthBuffer;
            uint nativeFptlBaseFeatureFlags;
            uint nativeMaterialBaseFeatureFlags;
            uint nativeMaterialFlagsMode;
            int numTilesFPTL;
            int numTilesFPTLX;
            int numTilesFPTLY;
            int numTilesClusterX;
            int numTilesClusterY;
            int viewCount;
            int capturedTotalLightCount;
            bool clusterNeedsDepth;
            uint envLightIndexShift;
            uint decalIndexShift;
            float nativeFptlInvM22;
            float nativeFptlInvM23;
            float nativeFptlInvM32;
            float nativeFptlInvM33;
            float nativeFptlScrSx;
            float nativeFptlScrSy;
            float nativeFptlScrCx;
            float nativeFptlScrCy;
            float nativeFptlScrCxOrtho;
            float nativeFptlScrCyOrtho;
            uint nativeFptlIsOrthographic;
            uint frameIndex;
            bool enableFeatureVariants;

            // Pre-compute native gates BEFORE the AddComputePass so its
            // UseBuffer declarations can use conditional access flags.
            // The full-fidelity gates (NativeLightListXxxEnabled) re-check
            // BufferHandle.IsValid() AFTER PrepareBuildGPULightListPassData
            // runs; the early-stage hints below only check the conditions
            // that are determinable from settings + totalLightCount, which
            // is sufficient because the BufferHandles are always valid
            // post-CreateBuffer when the relevant code path is active.
            bool earlyRunLightList = totalLightCount > 0 || hdCamera.xr.enabled;
            bool earlyEnableFeatureVariants = GetFeatureVariantsEnabled(hdCamera.frameSettings) && tileAndClusterData.hasTileBuffers;
            bool earlyRunBigTilePrepass = hdCamera.frameSettings.IsEnabled(FrameSettingsField.BigTilePrepass);
            bool earlyIsProjectionOblique = GeometryUtils.IsProjectionMatrixOblique(
                (hdCamera.xr.enabled ? hdCamera.xr.GetProjMatrix(0) : hdCamera.camera.projectionMatrix) * s_FlipMatrixLHSRHS);
            bool earlyNativeFptlProjectionSupported = !earlyIsProjectionOblique && hdCamera.viewCount == 1;
            bool earlyPrePassReady = NativeLightListPrePassReady();
            bool earlyNativeFptlCandidate =
                earlyRunLightList &&
                hdCamera.frameSettings.fptl &&
                tileAndClusterData.hasTileBuffers &&
                earlyNativeFptlProjectionSupported;
            bool earlyNativeIndirect =
                NativeLightListDiagnosticAllowsAnyIndirect() &&
                (((NativeLightListDiagnosticAllowsStandaloneMaterialIndirect() ||
                   (NativeLightListDiagnosticAllowsFptl() &&
                    earlyNativeFptlCandidate)) && earlyEnableFeatureVariants) ||
                 (NativeLightListDiagnosticAllowsFptl() && earlyNativeFptlCandidate)) &&
                NativeLightListIndirectReady() &&
                !NativeLightListVisualParityHold();
            bool nativeScreenAABBHint =
                NativeLightListDiagnosticAllowsScreenAABB() &&
                earlyRunLightList &&
                totalLightCount > 0 &&
                earlyNativeFptlProjectionSupported &&
                earlyPrePassReady;
            bool earlySupportsVolumetric = currentAsset.currentPlatformRenderPipelineSettings.supportVolumetrics;
            // Keep this hint in lock-step with NativeLightListBigTileEnabled.
            // If the hint and final gate disagree, RenderGraph may declare
            // the wrong writer for bigTileLightList / bigTileVolumetricLightList.
            // Volumetric-capable assets currently use HDRP's stock BigTile
            // kernel while we isolate the native volumetric-pack artifact.
            bool nativeBigTileHint = NativeLightListDiagnosticAllowsBigTile() && earlyRunLightList &&
                                     earlyRunBigTilePrepass &&
                                     earlyNativeFptlProjectionSupported && earlyPrePassReady &&
                                     !earlySupportsVolumetric;

            using (var builder = renderGraph.AddComputePass<BuildGPULightListPassData>("Build Light List", out var passData, ProfilingSampler.Get(HDProfileId.BuildLightList)))
            {
                builder.EnableAsyncCompute(hdCamera.frameSettings.BuildLightListRunsAsync());
                builder.AllowGlobalStateModification(true);

                PrepareBuildGPULightListPassData(renderGraph, builder, hdCamera, tileAndClusterData, ref constantBuffer, totalLightCount, depthStencilBuffer, stencilBufferCopy, gBuffer, passData, nativeScreenAABBHint, nativeBigTileHint);
                passData.nativeIndirect = NativeLightListIndirectEnabled(passData);
                passData.nativeFptl = NativeLightListFptlEnabled(passData);
                passData.nativeClustered = NativeLightListClusteredOverwriteEnabled(passData);
                passData.nativeBigTile = NativeLightListBigTileEnabled(passData);
                passData.nativeScreenAABB = NativeLightListScreenAABBEnabled(passData);
                passData.nativeFptlBaseFeatureFlags = passData.nativeFptl ? NativeLightListFptlBaseFeatureFlags(passData) : 0u;
                bool nativeTileFlagsWritten = passData.nativeFptl || NativeLightListTileFlagsWritten(passData);
                passData.nativeMaterialFlags = passData.nativeIndirect &&
                    (NativeLightListDiagnosticAllowsStandaloneMaterialIndirect() || passData.nativeFptl) &&
                    NativeLightListMaterialFlagsEnabled(passData, nativeTileFlagsWritten);
                passData.nativeMaterialBaseFeatureFlags = NativeLightListMaterialBaseFeatureFlags(passData, nativeTileFlagsWritten);
                passData.nativeMaterialFlagsMode = passData.nativeMaterialFlags ? NativeLightListMaterialFlagsMode(passData, nativeTileFlagsWritten) : 0u;
                if (!s_NativeLightListDiagnosticModeLogged && NativeLightListIndirectRuntimeSupported())
                {
                    s_NativeLightListDiagnosticModeLogged = true;
                    Debug.LogWarning($"[FSR3DeckNativeLightList] Diagnostic mode: {NativeLightListDiagnosticDescription()}.");
                }
                if (NativeLightListVisualParityHold() && !s_NativeLightListVisualParityHoldLogged)
                {
                    s_NativeLightListVisualParityHoldLogged = true;
                    Debug.LogWarning("[FSR3DeckNativeLightList] Native light-list replacement held for visual parity; using HDRP stock ScreenAABB/BigTile/FPTL/material/indirect kernels.");
                }
                if (passData.enableFeatureVariants && !passData.nativeIndirect &&
                    NativeLightListIndirectRuntimeSupported() &&
                    NativeLightListDiagnosticAllowsAnyIndirect() &&
                    !NativeLightListVisualParityHold() &&
                    !s_NativeLightListIndirectFallbackLogged)
                {
                    s_NativeLightListIndirectFallbackLogged = true;
                    Debug.LogWarning("[FSR3DeckNativeLightList] Native indirect/tile-list builder unavailable; using HDRP stock dispatch-indirect kernels.");
                }
                if (passData.nativeClustered &&
                    NativeLightListDiagnosticSkipsStockVoxel() &&
                    !s_NativeLightListSkipStockClusteredLogged)
                {
                    s_NativeLightListSkipStockClusteredLogged = true;
                    Debug.LogWarning("[FSR3DeckNativeLightList] Native clustered is performance-live; skipping HDRP stock VoxelLightListGeneration for this Deck contract.");
                }

                builder.SetRenderFunc(
                    (BuildGPULightListPassData data, ComputeGraphContext context) =>
                    {
                        bool tileFlagsWritten = false;

                        ClearLightLists(data, context.cmd);
                        GenerateLightsScreenSpaceAABBs(data, context.cmd, data.nativeScreenAABB);
                        BigTilePrepass(data, context.cmd, data.nativeBigTile);
                        BuildPerTileLightList(data, ref tileFlagsWritten, data.nativeFptl, context.cmd);
                        if (!(data.nativeClustered && NativeLightListDiagnosticSkipsStockVoxel()))
                            VoxelLightListGeneration(data, context.cmd);

                        BuildDispatchIndirectArguments(data, tileFlagsWritten, data.nativeIndirect, data.nativeMaterialFlags, context.cmd);
                    });

                output = passData.output;
                nativeIndirect = passData.nativeIndirect;
                nativeFptl = passData.nativeFptl;
                nativeClustered = passData.nativeClustered;
                nativeBigTile = passData.nativeBigTile;
                nativeScreenAABB = passData.nativeScreenAABB;
                nativeMaterialFlags = passData.nativeMaterialFlags;
                nativeFptlProjectionSupported = passData.nativeFptlProjectionSupported;
                useBigTileForFptl = passData.runBigTilePrepass;
                aabbBounds = passData.AABBBoundsBuffer;
                convexBounds = passData.convexBoundsBuffer;
                lightVolumeData = passData.lightVolumeDataBuffer;
                bigTileLightList = passData.output.bigTileLightList;
                bigTileVolumetricLightListCapture = passData.output.bigTileVolumetricLightList;
                depthBuffer = passData.depthBuffer;
                nativeFptlBaseFeatureFlags = passData.nativeFptlBaseFeatureFlags;
                nativeMaterialBaseFeatureFlags = passData.nativeMaterialBaseFeatureFlags;
                nativeMaterialFlagsMode = passData.nativeMaterialFlagsMode;
                enableFeatureVariants = passData.enableFeatureVariants;
                numTilesFPTL = passData.numTilesFPTL;
                numTilesFPTLX = passData.numTilesFPTLX;
                numTilesFPTLY = passData.numTilesFPTLY;
                numTilesClusterX = passData.numTilesClusterX;
                numTilesClusterY = passData.numTilesClusterY;
                viewCount = passData.viewCount;
                capturedTotalLightCount = passData.totalLightCount;
                clusterNeedsDepth = passData.clusterNeedsDepth;
                envLightIndexShift = passData.lightListCB._EnvLightIndexShift;
                decalIndexShift = passData.lightListCB._DecalIndexShift;
                nativeFptlInvM22 = passData.nativeFptlInvM22;
                nativeFptlInvM23 = passData.nativeFptlInvM23;
                nativeFptlInvM32 = passData.nativeFptlInvM32;
                nativeFptlInvM33 = passData.nativeFptlInvM33;
                nativeFptlScrSx = passData.nativeFptlScrSx;
                nativeFptlScrSy = passData.nativeFptlScrSy;
                nativeFptlScrCx = passData.nativeFptlScrCx;
                nativeFptlScrCy = passData.nativeFptlScrCy;
                nativeFptlScrCxOrtho = passData.nativeFptlScrCxOrtho;
                nativeFptlScrCyOrtho = passData.nativeFptlScrCyOrtho;
                nativeFptlIsOrthographic = passData.lightListCB.g_isOrthographic;
                frameIndex = (uint)hdCamera.GetCameraFrameCount();
            }

            // PrePass — runs BEFORE BuildGPULightList AddComputePass via
            // render-graph data dependency (PrePass writes AABBBoundsBuffer
            // / bigTileLightList; AddComputePass declared Read on those when
            // the gates were active). Only declared if at least one of the
            // PrePass kernels is active. Width/height for BigTile dispatch
            // is stashed via two int fields on this class because the lambda
            // can't safely capture hdCamera.
            if (nativeScreenAABB || nativeBigTile)
            {
                // Native ScreenAABB ports HDRP's scrbound.compute path, which
                // consumes g_mProjectionArr/g_mInvProjectionArr. Those are not
                // raw light-list projection matrices; they are the 0..1 depth
                // projection baked in PrepareBuildGPULightListPassData.
                var prePassTemp = new Matrix4x4();
                prePassTemp.SetRow(0, new Vector4(1.0f, 0.0f, 0.0f, 0.0f));
                prePassTemp.SetRow(1, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
                prePassTemp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
                prePassTemp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

                Matrix4x4 prePassProj    = prePassTemp * m_LightListProjMatrices[0];
                Matrix4x4 prePassInvProj = prePassProj.inverse;
                bool hasVolBigTile = nativeBigTile && bigTileVolumetricLightListCapture.IsValid();
                BufferHandle volBigTileForPrePass = hasVolBigTile
                    ? bigTileVolumetricLightListCapture
                    : default;
                RenderNativeLightListPrePass(renderGraph, aabbBounds, convexBounds, bigTileLightList,
                                             lightVolumeData, volBigTileForPrePass, hasVolBigTile,
                                             viewCount, capturedTotalLightCount, frameIndex,
                                             nativeScreenAABB, nativeBigTile,
                                             nativeFptlIsOrthographic, prePassProj, prePassInvProj,
                                             hdCamera.camera.nearClipPlane,
                                             hdCamera.camera.farClipPlane,
                                             hdCamera.actualWidth,
                                             hdCamera.actualHeight);
            }

            if (nativeIndirect)
            {
                RenderNativeLightListIndirect(renderGraph, output, gBuffer, stencilBufferCopy, depthBuffer, aabbBounds, convexBounds, lightVolumeData, bigTileLightList, hdCamera.actualWidth, hdCamera.actualHeight, numTilesFPTL, numTilesFPTLX, numTilesFPTLY, numTilesClusterX, numTilesClusterY, viewCount, capturedTotalLightCount, frameIndex, nativeFptl, nativeClustered, nativeFptlProjectionSupported, enableFeatureVariants, useBigTileForFptl, clusterNeedsDepth, hdCamera.camera.nearClipPlane, hdCamera.camera.farClipPlane, nativeFptlBaseFeatureFlags, envLightIndexShift, decalIndexShift, nativeFptlInvM22, nativeFptlInvM23, nativeFptlInvM32, nativeFptlInvM33, nativeFptlScrSx, nativeFptlScrSy, nativeFptlScrCx, nativeFptlScrCy, nativeFptlScrCxOrtho, nativeFptlScrCyOrtho, nativeFptlIsOrthographic, nativeMaterialFlags, nativeMaterialBaseFeatureFlags, nativeMaterialFlagsMode, nativeBigTile);
            }

            return output;
        }

        // PrePass data + render method (2026-05-12). Runs before HDRP's
        // BuildGPULightList AddComputePass via render-graph dependency
        // ordering (this pass declares Write on AABBBoundsBuffer +
        // bigTileLightList; the AddComputePass declared Read on those when
        // the native gates were active in PrepareBuildGPULightListPassData).
        class NativeLightListPrePassData
        {
            public BufferHandle aabbBounds;
            public BufferHandle convexBounds;
            public BufferHandle bigTileLightList;
            // _LightVolumeData and the volumetric BigTile output (only used
            // by the BigTile dispatch — null/invalid when nativeBigTile=false
            // or the scene doesn't have supportsVolumetric=true).
            public BufferHandle lightVolumeData;
            public BufferHandle bigTileVolumetricLightList;
            public bool hasVolumetricBigTile;
            public IntPtr callback;
            public IntPtr waitCallback;
            public int viewCount;
            public int totalLightCount;
            public uint frameIndex;
            public bool nativeScreenAABB;
            public bool nativeBigTile;
            public uint isOrthographic;
            public Matrix4x4 projMatrix;
            public Matrix4x4 invProjMatrix;
            public float nearPlane;
            public float farPlane;
            // 2026-05-12 audit fix: previously these were mutable instance
            // fields on HDRenderPipeline (hdCamera_actualWidthCache /
            // ...HeightCache), which races across cameras when render-graph
            // delays a pass's execution. Per-pass-data fields are immutable
            // post-construction so the lambda always sees the values
            // captured when the pass was declared.
            public int actualWidth;
            public int actualHeight;
        }

        void RenderNativeLightListPrePass(RenderGraph renderGraph,
                                          BufferHandle aabbBounds,
                                          BufferHandle convexBounds,
                                          BufferHandle bigTileLightList,
                                          BufferHandle lightVolumeData,
                                          BufferHandle bigTileVolumetricLightList,
                                          bool hasVolumetricBigTile,
                                          int viewCount,
                                          int totalLightCount,
                                          uint frameIndex,
                                          bool nativeScreenAABB,
                                          bool nativeBigTile,
                                          uint isOrthographic,
                                          Matrix4x4 projMatrix,
                                          Matrix4x4 invProjMatrix,
                                          float nearPlane,
                                          float farPlane,
                                          int actualWidth,
                                          int actualHeight)
        {
            using (var builder = renderGraph.AddUnsafePass<NativeLightListPrePassData>(
                "FSR3 Native LightList PrePass", out var passData,
                ProfilingSampler.Get(HDProfileId.BuildLightList)))
            {
                passData.aabbBounds = aabbBounds;
                passData.convexBounds = convexBounds;
                passData.bigTileLightList = bigTileLightList;
                passData.lightVolumeData = lightVolumeData;
                passData.bigTileVolumetricLightList = bigTileVolumetricLightList;
                passData.hasVolumetricBigTile = hasVolumetricBigTile;
                passData.callback = GetNativeLightListPrePassDispatchCallback();
                passData.waitCallback = s_NativeLightListPrePassWaitCallback;
                passData.viewCount = viewCount;
                passData.totalLightCount = totalLightCount;
                passData.frameIndex = frameIndex;
                passData.nativeScreenAABB = nativeScreenAABB;
                passData.nativeBigTile = nativeBigTile;
                passData.isOrthographic = isOrthographic;
                passData.projMatrix = projMatrix;
                passData.invProjMatrix = invProjMatrix;
                passData.nearPlane = nearPlane;
                passData.farPlane = farPlane;
                passData.actualWidth = actualWidth;
                passData.actualHeight = actualHeight;

                // PrePass writes AABBBoundsBuffer (via ScreenAABB) and
                // bigTileLightList + bigTileVolumetricLightList (via BigTile).
                // Reads convexBoundsBuffer + lightVolumeData.
                if (nativeScreenAABB)
                    builder.UseBuffer(passData.aabbBounds, AccessFlags.Write);
                if (nativeBigTile)
                {
                    if (!nativeScreenAABB)
                        builder.UseBuffer(passData.aabbBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.bigTileLightList, AccessFlags.Write);
                    builder.UseBuffer(passData.lightVolumeData, AccessFlags.Read);
                    if (hasVolumetricBigTile)
                        builder.UseBuffer(passData.bigTileVolumetricLightList, AccessFlags.Write);
                }
                builder.UseBuffer(passData.convexBounds, AccessFlags.Read);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (NativeLightListPrePassData data, UnsafeGraphContext ctx) =>
                    {
                        if (data.callback == IntPtr.Zero)
                            return;

                        // BufferHandle → GraphicsBuffer is implicit (matches
                        // the IndirectPass pattern below). Skip when the
                        // BufferHandle isn't valid (the gate logic should
                        // prevent this but be defensive).
                        GraphicsBuffer convexBuf = data.convexBounds;
                        GraphicsBuffer aabbBuf = data.aabbBounds;
                        GraphicsBuffer bigTileBuf = data.bigTileLightList;
                        GraphicsBuffer lightVolumeBuf = data.lightVolumeData;
                        GraphicsBuffer volBigTileBuf = data.bigTileVolumetricLightList;
                        if (convexBuf == null) return;

                        NativeLightListIndirectParams nativeParams = default;
                        nativeParams.convexBounds = convexBuf.GetNativeBufferPtr();
                        nativeParams.aabbBounds = aabbBuf != null ? aabbBuf.GetNativeBufferPtr() : IntPtr.Zero;
                        nativeParams.bigTileLightList = (data.nativeBigTile && bigTileBuf != null) ? bigTileBuf.GetNativeBufferPtr() : IntPtr.Zero;
                        nativeParams.lightVolumeData = (data.nativeBigTile && lightVolumeBuf != null) ? lightVolumeBuf.GetNativeBufferPtr() : IntPtr.Zero;
                        nativeParams.bigTileVolumetricLightList =
                            (data.nativeBigTile && data.hasVolumetricBigTile && volBigTileBuf != null)
                                ? volBigTileBuf.GetNativeBufferPtr() : IntPtr.Zero;
                        SetNativeScreenAABBProjection(ref nativeParams, data.projMatrix);
                        nativeParams.viewCount = (uint)Mathf.Max(1, data.viewCount);
                        nativeParams.totalLightCount = (uint)Mathf.Max(0, data.totalLightCount);
                        nativeParams.frameIndex = data.frameIndex;
                        nativeParams.nativeScreenAABB = data.nativeScreenAABB ? 1u : 0u;
                        nativeParams.nativeBigTile = data.nativeBigTile ? 1u : 0u;
                        nativeParams.fptlIsOrthographic = data.isOrthographic;
                        nativeParams.nearPlane = data.nearPlane;
                        nativeParams.farPlane = data.farPlane;
                        // BigTile shader needs width/height from screen size
                        // — pass via the same fields IndirectPass uses.
                        // Source of truth lives in passData (immutable
                        // post-construction; no cross-camera race).
                        nativeParams.width = (uint)Mathf.Max(1, data.actualWidth);
                        nativeParams.height = (uint)Mathf.Max(1, data.actualHeight);

                        if (nativeParams.convexBounds == IntPtr.Zero)
                            return;
                        if (data.nativeScreenAABB && nativeParams.aabbBounds == IntPtr.Zero)
                            return;
                        if (data.nativeBigTile &&
                            (nativeParams.bigTileLightList == IntPtr.Zero ||
                             nativeParams.lightVolumeData == IntPtr.Zero))
                            return;
                        // When supportsVolumetric is true, the volumetric
                        // buffer MUST be present — gate logic ensures it.
                        if (data.nativeBigTile && data.hasVolumetricBigTile &&
                            nativeParams.bigTileVolumetricLightList == IntPtr.Zero)
                            return;

                        FSR3_NativeLightListPrePass_SetParams(nativeParams);
                        ctx.cmd.IssuePluginEvent(data.callback, kNativeLightListPrePassEventId);

                        if (data.waitCallback != IntPtr.Zero)
                            ctx.cmd.IssuePluginEvent(data.waitCallback, kNativeLightListPrePassWaitEventId);
                    });
            }
        }

        class NativeLightListIndirectPassData
        {
            public BufferHandle tileFeatureFlags;
            public BufferHandle lightList;
            public BufferHandle parityTileFeatureFlags;
            public BufferHandle parityLightList;
            public BufferHandle parityClusteredLightList;
            public BufferHandle parityClusteredOffset;
            public BufferHandle parityClusteredLogBase;
            public BufferHandle parityClusteredAtomic;
            public BufferHandle nativeClusteredLightList;
            public BufferHandle nativeClusteredOffset;
            public BufferHandle nativeClusteredLogBase;
            public BufferHandle nativeClusteredAtomic;
            public BufferHandle tileList;
            public BufferHandle dispatchIndirectBuffer;
            public BufferHandle aabbBounds;
            public BufferHandle convexBounds;
            public BufferHandle lightVolumeData;
            public BufferHandle bigTileLightList;
            public TextureHandle gbuffer2;
            public TextureHandle stencil;
            public TextureHandle depth;
            public IntPtr callback;
            public IntPtr waitCallback;
            public int numTiles;
            public int numTilesX;
            public int numTilesY;
            public int clusterNumTilesX;
            public int clusterNumTilesY;
            public int viewCount;
            public int width;
            public int height;
            public int totalLightCount;
            public uint frameIndex;
            public bool nativeFptl;
            public bool enableFeatureVariants;
            public bool useBigTileForFptl;
            public bool clusterNeedsDepth;
            public float nearPlane;
            public float farPlane;
            public uint fptlBaseFeatureFlags;
            public uint envLightIndexShift;
            public uint decalIndexShift;
            public float fptlInvM22;
            public float fptlInvM23;
            public float fptlInvM32;
            public float fptlInvM33;
            public float fptlScrSx;
            public float fptlScrSy;
            public float fptlScrCx;
            public float fptlScrCy;
            public float fptlScrCxOrtho;
            public float fptlScrCyOrtho;
            public uint fptlIsOrthographic;
            public uint materialBaseFeatureFlags;
            public uint materialFlagsMode;
            public bool nativeBigTile;
            public bool parityFptl;
            public bool parityClustered;
            public bool nativeClustered;
            public int parityCompareDwords;
            public int parityClusteredOffsetDwords;
            public int parityClusteredListDwords;
        }

        void RenderNativeLightListIndirect(RenderGraph renderGraph, BuildGPULightListOutput output, GBufferOutput gBuffer, TextureHandle stencilTexture, TextureHandle depthTexture, BufferHandle aabbBounds, BufferHandle convexBounds, BufferHandle lightVolumeData, BufferHandle bigTileLightList, int width, int height, int numTiles, int numTilesX, int numTilesY, int clusterNumTilesX, int clusterNumTilesY, int viewCount, int totalLightCount, uint frameIndex, bool nativeFptl, bool nativeClustered, bool nativeFptlProjectionSupported, bool enableFeatureVariants, bool useBigTileForFptl, bool clusterNeedsDepth, float nearPlane, float farPlane, uint nativeFptlBaseFeatureFlags, uint envLightIndexShift, uint decalIndexShift, float nativeFptlInvM22, float nativeFptlInvM23, float nativeFptlInvM32, float nativeFptlInvM33, float nativeFptlScrSx, float nativeFptlScrSy, float nativeFptlScrCx, float nativeFptlScrCy, float nativeFptlScrCxOrtho, float nativeFptlScrCyOrtho, uint nativeFptlIsOrthographic, bool nativeMaterialFlags, uint nativeMaterialBaseFeatureFlags, uint nativeMaterialFlagsMode, bool nativeBigTile)
        {
            IntPtr waitCallback;
            bool parityFptl = NativeLightListFptlParityProbeEnabled(nativeFptl, totalLightCount > 0, nativeFptlProjectionSupported, totalLightCount, output.lightList, output.tileFeatureFlags, depthTexture, aabbBounds, convexBounds, lightVolumeData);
            bool parityClustered = !nativeClustered &&
                NativeLightListClusteredParityProbeEnabled(nativeFptlProjectionSupported, totalLightCount > 0, clusterNeedsDepth, totalLightCount, output.perVoxelOffset, output.perVoxelLightLists, depthTexture, aabbBounds, convexBounds, lightVolumeData, bigTileLightList, useBigTileForFptl);
            BufferHandle parityLightList = default;
            BufferHandle parityTileFeatureFlags = default;
            BufferHandle parityClusteredLightList = default;
            BufferHandle parityClusteredOffset = default;
            BufferHandle parityClusteredLogBase = default;
            BufferHandle parityClusteredAtomic = default;
            BufferHandle nativeClusteredAtomic = default;
            int parityCompareDwords = 0;
            int parityClusteredOffsetDwords = 0;
            int parityClusteredListDwords = 0;
            if (parityFptl)
            {
                var parityLightListDesc = renderGraph.GetBufferDesc(output.lightList);
                parityLightListDesc.name = "Native FPTL Parity LightList";
                parityLightList = renderGraph.CreateBuffer(parityLightListDesc);

                var parityTileFeatureFlagsDesc = renderGraph.GetBufferDesc(output.tileFeatureFlags);
                parityTileFeatureFlagsDesc.name = "Native FPTL Parity TileFeatureFlags";
                parityTileFeatureFlags = renderGraph.CreateBuffer(parityTileFeatureFlagsDesc);

                parityCompareDwords = Mathf.Max(0, numTiles * viewCount * (int)LightCategory.Count * InternalLightCullingDefs.s_LightDwordPerFptlTile);
            }
            if (parityClustered)
            {
                var parityClusteredLightListDesc = renderGraph.GetBufferDesc(output.perVoxelLightLists);
                parityClusteredLightListDesc.name = "Native Clustered Parity PerVoxelLightList";
                parityClusteredLightList = renderGraph.CreateBuffer(parityClusteredLightListDesc);
                parityClusteredListDwords = parityClusteredLightListDesc.count;

                var parityClusteredOffsetDesc = renderGraph.GetBufferDesc(output.perVoxelOffset);
                parityClusteredOffsetDesc.name = "Native Clustered Parity PerVoxelOffset";
                parityClusteredOffset = renderGraph.CreateBuffer(parityClusteredOffsetDesc);

                parityClusteredAtomic = renderGraph.CreateBuffer(new BufferDesc(1, sizeof(uint)) { name = "Native Clustered Parity Atomic" });

                int numClusterTiles = Mathf.Max(0, clusterNumTilesX * clusterNumTilesY);
                int numClusters = 1 << k_Log2NumClusters;
                parityClusteredOffsetDwords = Mathf.Max(0, viewCount * (int)LightCategory.Count * numClusters * numClusterTiles);

                if (clusterNeedsDepth && output.perTileLogBaseTweak.IsValid())
                {
                    var parityClusteredLogBaseDesc = renderGraph.GetBufferDesc(output.perTileLogBaseTweak);
                    parityClusteredLogBaseDesc.name = "Native Clustered Parity PerTileLogBaseTweak";
                    parityClusteredLogBase = renderGraph.CreateBuffer(parityClusteredLogBaseDesc);
                }
            }
            if (nativeClustered)
                nativeClusteredAtomic = renderGraph.CreateBuffer(new BufferDesc(1, sizeof(uint)) { name = "Native Clustered Live Atomic" });

            using (var builder = renderGraph.AddUnsafePass<NativeLightListIndirectPassData>("FSR3 Native LightList Indirect", out var passData, ProfilingSampler.Get(HDProfileId.BuildLightList)))
            {
                passData.tileFeatureFlags = output.tileFeatureFlags;
                passData.lightList = output.lightList;
                passData.parityTileFeatureFlags = parityTileFeatureFlags;
                passData.parityLightList = parityLightList;
                passData.parityClusteredLightList = parityClusteredLightList;
                passData.parityClusteredOffset = parityClusteredOffset;
                passData.parityClusteredLogBase = parityClusteredLogBase;
                passData.parityClusteredAtomic = parityClusteredAtomic;
                passData.nativeClusteredLightList = output.perVoxelLightLists;
                passData.nativeClusteredOffset = output.perVoxelOffset;
                passData.nativeClusteredLogBase = output.perTileLogBaseTweak;
                passData.nativeClusteredAtomic = nativeClusteredAtomic;
                passData.tileList = output.tileList;
                passData.dispatchIndirectBuffer = output.dispatchIndirectBuffer;
                passData.aabbBounds = aabbBounds;
                passData.convexBounds = convexBounds;
                passData.lightVolumeData = lightVolumeData;
                passData.bigTileLightList = bigTileLightList;
                passData.gbuffer2 = nativeMaterialFlags && gBuffer.gBufferCount > 2 ? gBuffer.mrt[2] : renderGraph.defaultResources.blackTextureXR;
                passData.stencil = nativeMaterialFlags && stencilTexture.IsValid() ? stencilTexture : renderGraph.defaultResources.blackUIntTextureXR;
                passData.depth = (nativeFptl || parityFptl || parityClustered || nativeClustered) && depthTexture.IsValid() ? depthTexture : renderGraph.defaultResources.blackTextureXR;
                passData.callback = GetNativeLightListIndirectDispatchCallback();
                passData.waitCallback = s_NativeLightListIndirectWaitCallback;
                passData.numTiles = numTiles;
                passData.numTilesX = numTilesX;
                passData.numTilesY = numTilesY;
                passData.clusterNumTilesX = clusterNumTilesX;
                passData.clusterNumTilesY = clusterNumTilesY;
                passData.viewCount = viewCount;
                passData.width = width;
                passData.height = height;
                passData.totalLightCount = totalLightCount;
                passData.frameIndex = frameIndex;
                passData.nativeFptl = nativeFptl;
                passData.enableFeatureVariants = enableFeatureVariants;
                passData.useBigTileForFptl = (nativeFptl || parityFptl || parityClustered || nativeClustered) && useBigTileForFptl;
                passData.clusterNeedsDepth = clusterNeedsDepth;
                passData.nearPlane = nearPlane;
                passData.farPlane = farPlane;
                passData.fptlBaseFeatureFlags = nativeFptlBaseFeatureFlags;
                passData.envLightIndexShift = envLightIndexShift;
                passData.decalIndexShift = decalIndexShift;
                passData.fptlInvM22 = nativeFptlInvM22;
                passData.fptlInvM23 = nativeFptlInvM23;
                passData.fptlInvM32 = nativeFptlInvM32;
                passData.fptlInvM33 = nativeFptlInvM33;
                passData.fptlScrSx = nativeFptlScrSx;
                passData.fptlScrSy = nativeFptlScrSy;
                passData.fptlScrCx = nativeFptlScrCx;
                passData.fptlScrCy = nativeFptlScrCy;
                passData.fptlScrCxOrtho = nativeFptlScrCxOrtho;
                passData.fptlScrCyOrtho = nativeFptlScrCyOrtho;
                passData.fptlIsOrthographic = nativeFptlIsOrthographic;
                passData.materialBaseFeatureFlags = nativeMaterialBaseFeatureFlags;
                passData.materialFlagsMode = nativeMaterialFlags ? nativeMaterialFlagsMode : 0u;
                passData.nativeBigTile = nativeBigTile;
                passData.parityFptl = parityFptl;
                passData.parityClustered = parityClustered;
                passData.nativeClustered = nativeClustered;
                passData.parityCompareDwords = parityCompareDwords;
                passData.parityClusteredOffsetDwords = parityClusteredOffsetDwords;
                passData.parityClusteredListDwords = parityClusteredListDwords;
                waitCallback = passData.waitCallback;

                builder.UseBuffer(passData.tileFeatureFlags,
                    (nativeMaterialFlags || (nativeFptl && enableFeatureVariants)) ? AccessFlags.ReadWrite : AccessFlags.Read);
                if (nativeFptl)
                {
                    builder.UseBuffer(passData.lightList, AccessFlags.Write);
                    builder.UseBuffer(passData.aabbBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.convexBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.lightVolumeData, AccessFlags.Read);
                    if (passData.useBigTileForFptl)
                        builder.UseBuffer(passData.bigTileLightList, AccessFlags.Read);
                    builder.UseTexture(passData.depth, AccessFlags.Read);
                }
                if (parityFptl)
                {
                    builder.UseBuffer(passData.lightList, AccessFlags.Read);
                    builder.UseBuffer(passData.parityLightList, AccessFlags.Write);
                    builder.UseBuffer(passData.parityTileFeatureFlags, AccessFlags.Write);
                    builder.UseBuffer(passData.aabbBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.convexBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.lightVolumeData, AccessFlags.Read);
                    if (passData.useBigTileForFptl)
                        builder.UseBuffer(passData.bigTileLightList, AccessFlags.Read);
                    builder.UseTexture(passData.depth, AccessFlags.Read);
                }
                if (parityClustered)
                {
                    builder.UseBuffer(output.perVoxelOffset, AccessFlags.Read);
                    builder.UseBuffer(output.perVoxelLightLists, AccessFlags.Read);
                    builder.UseBuffer(passData.parityClusteredOffset, AccessFlags.Write);
                    builder.UseBuffer(passData.parityClusteredLightList, AccessFlags.Write);
                    builder.UseBuffer(passData.parityClusteredAtomic, AccessFlags.Write);
                    builder.UseBuffer(passData.aabbBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.convexBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.lightVolumeData, AccessFlags.Read);
                    if (useBigTileForFptl)
                        builder.UseBuffer(passData.bigTileLightList, AccessFlags.Read);
                    builder.UseTexture(passData.depth, AccessFlags.Read);
                    if (clusterNeedsDepth && passData.parityClusteredLogBase.IsValid())
                        builder.UseBuffer(passData.parityClusteredLogBase, AccessFlags.Write);
                }
                if (nativeClustered)
                {
                    builder.UseBuffer(passData.nativeClusteredOffset, AccessFlags.ReadWrite);
                    builder.UseBuffer(passData.nativeClusteredLightList, AccessFlags.ReadWrite);
                    builder.UseBuffer(passData.nativeClusteredAtomic, AccessFlags.Write);
                    builder.UseBuffer(passData.aabbBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.convexBounds, AccessFlags.Read);
                    builder.UseBuffer(passData.lightVolumeData, AccessFlags.Read);
                    if (useBigTileForFptl)
                        builder.UseBuffer(passData.bigTileLightList, AccessFlags.Read);
                    builder.UseTexture(passData.depth, AccessFlags.Read);
                    if (clusterNeedsDepth && passData.nativeClusteredLogBase.IsValid())
                        builder.UseBuffer(passData.nativeClusteredLogBase, AccessFlags.ReadWrite);
                }
                if (passData.enableFeatureVariants)
                {
                    builder.UseBuffer(passData.tileList, AccessFlags.Write);
                    builder.UseBuffer(passData.dispatchIndirectBuffer, AccessFlags.Write);
                    builder.UseTexture(passData.gbuffer2, AccessFlags.Read);
                    builder.UseTexture(passData.stencil, AccessFlags.Read);
                }
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (NativeLightListIndirectPassData data, UnsafeGraphContext ctx) =>
                    {
                        if (data.callback == IntPtr.Zero)
                            return;

                        GraphicsBuffer tileFeatureFlags = data.tileFeatureFlags;
                        GraphicsBuffer lightList = data.lightList;
                        GraphicsBuffer parityTileFeatureFlags = data.parityTileFeatureFlags;
                        GraphicsBuffer parityLightList = data.parityLightList;
                        GraphicsBuffer parityClusteredLightList = data.parityClusteredLightList;
                        GraphicsBuffer parityClusteredOffset = data.parityClusteredOffset;
                        GraphicsBuffer parityClusteredLogBase = data.parityClusteredLogBase;
                        GraphicsBuffer parityClusteredAtomic = data.parityClusteredAtomic;
                        GraphicsBuffer nativeClusteredLightList = data.nativeClusteredLightList;
                        GraphicsBuffer nativeClusteredOffset = data.nativeClusteredOffset;
                        GraphicsBuffer nativeClusteredLogBase = data.nativeClusteredLogBase;
                        GraphicsBuffer nativeClusteredAtomic = data.nativeClusteredAtomic;
                        GraphicsBuffer tileList = data.tileList;
                        GraphicsBuffer dispatchIndirectBuffer = data.dispatchIndirectBuffer;
                        GraphicsBuffer aabbBounds = data.aabbBounds;
                        GraphicsBuffer convexBounds = data.convexBounds;
                        GraphicsBuffer lightVolumeData = data.lightVolumeData;
                        GraphicsBuffer bigTileLightList = data.bigTileLightList;
                        Texture gbuffer2 = data.gbuffer2;
                        Texture stencil = data.stencil;
                        Texture depth = data.depth;
                        if (tileFeatureFlags == null)
                            return;
                        if (data.enableFeatureVariants &&
                            (tileList == null || dispatchIndirectBuffer == null ||
                             gbuffer2 == null || stencil == null))
                            return;
                        if (data.nativeFptl &&
                            (lightList == null || aabbBounds == null || convexBounds == null || lightVolumeData == null ||
                             depth == null || (data.useBigTileForFptl && bigTileLightList == null)))
                            return;
                        if (data.parityFptl &&
                            (lightList == null || parityLightList == null || parityTileFeatureFlags == null ||
                             aabbBounds == null || convexBounds == null || lightVolumeData == null ||
                             depth == null || (data.useBigTileForFptl && bigTileLightList == null)))
                            return;
                        if (data.parityClustered &&
                            (parityClusteredLightList == null || parityClusteredOffset == null || parityClusteredAtomic == null ||
                             aabbBounds == null || convexBounds == null || lightVolumeData == null ||
                             depth == null || (data.clusterNeedsDepth && parityClusteredLogBase == null) ||
                             (data.clusterNeedsDepth && data.parityClusteredOffsetDwords <= 0) ||
                             (data.useBigTileForFptl && bigTileLightList == null)))
                            return;
                        if (data.nativeClustered &&
                            (nativeClusteredLightList == null || nativeClusteredOffset == null || nativeClusteredAtomic == null ||
                             aabbBounds == null || convexBounds == null || lightVolumeData == null ||
                             depth == null || (data.clusterNeedsDepth && nativeClusteredLogBase == null) ||
                             (data.useBigTileForFptl && bigTileLightList == null)))
                            return;

                        NativeLightListIndirectParams nativeParams = default;
                        nativeParams.tileFeatureFlags = tileFeatureFlags.GetNativeBufferPtr();
                        if (data.enableFeatureVariants)
                        {
                            nativeParams.tileList = tileList.GetNativeBufferPtr();
                            nativeParams.dispatchIndirectBuffer = dispatchIndirectBuffer.GetNativeBufferPtr();
                            nativeParams.gbuffer2 = gbuffer2.GetNativeTexturePtr();
                            nativeParams.stencil = stencil.GetNativeTexturePtr();
                        }
                        if (data.nativeFptl)
                        {
                            nativeParams.aabbBounds = aabbBounds.GetNativeBufferPtr();
                            nativeParams.convexBounds = convexBounds.GetNativeBufferPtr();
                            nativeParams.lightVolumeData = lightVolumeData.GetNativeBufferPtr();
                            nativeParams.bigTileLightList = data.useBigTileForFptl ? bigTileLightList.GetNativeBufferPtr() : IntPtr.Zero;
                            nativeParams.depth = depth.GetNativeTexturePtr();
                            nativeParams.lightList = lightList.GetNativeBufferPtr();
                        }
                        if (data.parityFptl)
                        {
                            nativeParams.aabbBounds = aabbBounds.GetNativeBufferPtr();
                            nativeParams.convexBounds = convexBounds.GetNativeBufferPtr();
                            nativeParams.lightVolumeData = lightVolumeData.GetNativeBufferPtr();
                            nativeParams.bigTileLightList = data.useBigTileForFptl ? bigTileLightList.GetNativeBufferPtr() : IntPtr.Zero;
                            nativeParams.depth = depth.GetNativeTexturePtr();
                            nativeParams.parityLightList = parityLightList.GetNativeBufferPtr();
                            nativeParams.parityTileFeatureFlags = parityTileFeatureFlags.GetNativeBufferPtr();
                            nativeParams.parityFptl = 1u;
                        }
                        if (data.parityClustered || data.nativeClustered)
                        {
                            GraphicsBuffer clusteredLightList = data.nativeClustered ? nativeClusteredLightList : parityClusteredLightList;
                            GraphicsBuffer clusteredOffset = data.nativeClustered ? nativeClusteredOffset : parityClusteredOffset;
                            GraphicsBuffer clusteredLogBase = data.nativeClustered ? nativeClusteredLogBase : parityClusteredLogBase;
                            GraphicsBuffer clusteredAtomic = data.nativeClustered ? nativeClusteredAtomic : parityClusteredAtomic;

                            nativeParams.aabbBounds = aabbBounds.GetNativeBufferPtr();
                            nativeParams.convexBounds = convexBounds.GetNativeBufferPtr();
                            nativeParams.lightVolumeData = lightVolumeData.GetNativeBufferPtr();
                            nativeParams.bigTileLightList = data.useBigTileForFptl ? bigTileLightList.GetNativeBufferPtr() : IntPtr.Zero;
                            nativeParams.depth = depth.GetNativeTexturePtr();
                            nativeParams.parityClusteredLightList = clusteredLightList.GetNativeBufferPtr();
                            nativeParams.parityClusteredOffset = clusteredOffset.GetNativeBufferPtr();
                            nativeParams.parityClusteredLogBase =
                                data.clusterNeedsDepth && clusteredLogBase != null
                                    ? clusteredLogBase.GetNativeBufferPtr()
                                    : IntPtr.Zero;
                            nativeParams.parityClusteredAtomic = clusteredAtomic.GetNativeBufferPtr();
                            // Bit-pack clustered dispatch flags (2026-05-22
                            // cleanup). RUN is set because we're inside the
                            // `data.parityClustered || data.nativeClustered`
                            // block. LIVE / PARITY-PROBE come from the
                            // authoritative C# booleans. Bit values mirror the
                            // native kClusteredFlag* constants:
                            //   bit0=run, bit1=live, bit2=parityProbe.
                            uint clusteredFlags = kClusteredFlagRunDispatch;
                            if (data.nativeClustered) clusteredFlags |= kClusteredFlagIsLive;
                            if (data.parityClustered) clusteredFlags |= kClusteredFlagIsParityProbe;
                            nativeParams.clusteredDispatchFlags = clusteredFlags;
                            nativeParams.clusterNumTilesX = (uint)Mathf.Max(1, data.clusterNumTilesX);
                            nativeParams.clusterNumTilesY = (uint)Mathf.Max(1, data.clusterNumTilesY);
                            nativeParams.clusterNeedsDepth = data.clusterNeedsDepth ? 1u : 0u;
                            nativeParams.clusterUseBigTile = data.useBigTileForFptl ? 1u : 0u;
                            nativeParams.clusteredLayeredLightListEntryCount = (uint)Mathf.Max(0, clusteredLightList.count);
                            nativeParams.clusteredLayeredOffsetEntryCount = (uint)Mathf.Max(0, clusteredOffset.count);
                            nativeParams.clusteredLogBaseEntryCount = (uint)Mathf.Max(0, data.clusterNeedsDepth && clusteredLogBase != null ? clusteredLogBase.count : 0);
                            nativeParams.clusteredLog2NumClusters = (uint)k_Log2NumClusters;
                            nativeParams.clusteredClustBase = k_ClustLogBase;
                        }
                        nativeParams.numTiles = (uint)Mathf.Max(1, data.numTiles);
                        nativeParams.numTilesX = (uint)Mathf.Max(1, data.numTilesX);
                        nativeParams.viewCount = (uint)Mathf.Max(1, data.viewCount);
                        nativeParams.frameIndex = data.frameIndex;
                        nativeParams.width = (uint)Mathf.Max(1, data.width);
                        nativeParams.height = (uint)Mathf.Max(1, data.height);
                        nativeParams.baseFeatureFlags = data.materialBaseFeatureFlags;
                        nativeParams.materialFlagsMode = data.materialFlagsMode;
                        nativeParams.numTilesY = (uint)Mathf.Max(1, data.numTilesY);
                        nativeParams.totalLightCount = (uint)Mathf.Max(0, data.totalLightCount);
                        nativeParams.nativeFptl = data.nativeFptl ? 1u : 0u;
                        nativeParams.useBigTileForFptl = data.useBigTileForFptl ? 1u : 0u;
                        nativeParams.fptlBaseFeatureFlags = data.fptlBaseFeatureFlags;
                        nativeParams.envLightIndexShift = data.envLightIndexShift;
                        nativeParams.decalIndexShift = data.decalIndexShift;
                        nativeParams.fptlInvM22 = data.fptlInvM22;
                        nativeParams.fptlInvM23 = data.fptlInvM23;
                        nativeParams.fptlInvM32 = data.fptlInvM32;
                        nativeParams.fptlInvM33 = data.fptlInvM33;
                        nativeParams.fptlScrSx = data.fptlScrSx;
                        nativeParams.fptlScrSy = data.fptlScrSy;
                        nativeParams.fptlScrCx = data.fptlScrCx;
                        nativeParams.fptlScrCy = data.fptlScrCy;
                        nativeParams.fptlScrCxOrtho = data.fptlScrCxOrtho;
                        nativeParams.fptlScrCyOrtho = data.fptlScrCyOrtho;
                        nativeParams.fptlIsOrthographic = data.fptlIsOrthographic;
                        nativeParams.nativeBigTile = data.nativeBigTile ? 1u : 0u;
                        nativeParams.enableFeatureVariants = data.enableFeatureVariants ? 1u : 0u;
                        nativeParams.nearPlane = data.nearPlane;
                        nativeParams.farPlane = data.farPlane;

                        if (nativeParams.tileFeatureFlags == IntPtr.Zero)
                            return;
                        if (data.enableFeatureVariants &&
                            (nativeParams.tileList == IntPtr.Zero ||
                             nativeParams.dispatchIndirectBuffer == IntPtr.Zero ||
                             nativeParams.gbuffer2 == IntPtr.Zero ||
                             nativeParams.stencil == IntPtr.Zero))
                            return;
                        if (data.nativeFptl &&
                            (nativeParams.aabbBounds == IntPtr.Zero ||
                             nativeParams.convexBounds == IntPtr.Zero ||
                             nativeParams.lightVolumeData == IntPtr.Zero ||
                             nativeParams.depth == IntPtr.Zero ||
                             nativeParams.lightList == IntPtr.Zero ||
                             (data.useBigTileForFptl && nativeParams.bigTileLightList == IntPtr.Zero)))
                            return;
                        if (data.parityFptl &&
                            (nativeParams.aabbBounds == IntPtr.Zero ||
                             nativeParams.convexBounds == IntPtr.Zero ||
                             nativeParams.lightVolumeData == IntPtr.Zero ||
                             nativeParams.depth == IntPtr.Zero ||
                             nativeParams.parityLightList == IntPtr.Zero ||
                             nativeParams.parityTileFeatureFlags == IntPtr.Zero ||
                             (data.useBigTileForFptl && nativeParams.bigTileLightList == IntPtr.Zero)))
                            return;
                        if ((data.parityClustered || data.nativeClustered) &&
                            (nativeParams.aabbBounds == IntPtr.Zero ||
                             nativeParams.convexBounds == IntPtr.Zero ||
                             nativeParams.lightVolumeData == IntPtr.Zero ||
                             nativeParams.depth == IntPtr.Zero ||
                             nativeParams.parityClusteredLightList == IntPtr.Zero ||
                             nativeParams.parityClusteredOffset == IntPtr.Zero ||
                             nativeParams.parityClusteredAtomic == IntPtr.Zero ||
                             (data.clusterNeedsDepth && nativeParams.parityClusteredLogBase == IntPtr.Zero) ||
                             (data.useBigTileForFptl && nativeParams.bigTileLightList == IntPtr.Zero)))
                            return;

                        FSR3_NativeLightListIndirect_SetParams(nativeParams);
                        ctx.cmd.IssuePluginEvent(data.callback, kNativeLightListIndirectEventId);

                        if ((s_NativeLightListIndirectLogCounter++ % 120) == 0)
                        {
                            Debug.Log($"[FSR3DeckNativeLightList] native indirect dispatch frame={nativeParams.frameIndex} tiles={nativeParams.numTiles} tilesX={nativeParams.numTilesX} views={nativeParams.viewCount} nativeFptl={nativeParams.nativeFptl} nativeClustered={(data.nativeClustered ? 1 : 0)} parityFptl={nativeParams.parityFptl} parityClustered={(data.parityClustered ? 1 : 0)} nativeBigTile={nativeParams.nativeBigTile} featureVariants={nativeParams.enableFeatureVariants} lights={nativeParams.totalLightCount} materialMode={nativeParams.materialFlagsMode}");
                        }
                    });
            }

            // Phase 1c wait-migration (2026-05-16): the producer-adjacent wait
            // pass collapsed the parallel window between LightList Indirect
            // compute (on slot 3) and any HDRP graphics work that runs between
            // the producer dispatch and the first true graphics consumer of
            // lightList / perVoxelLightLists / tileList / dispatchIndirectBuffer
            // (RenderDeferredLighting on graphics queue). Cross-compute
            // consumers (currently NativeContactShadows) wait DIRECTLY on the
            // producer's computeDoneAux semaphore via the Phase 1b cross-
            // compute substrate, so the graphics-queue wait pass only needs
            // to fire before the FIRST true graphics consumer. Capture the
            // wait state here; HDRenderPipeline.RenderGraph.cs invokes
            // EmitDeferredNativeLightListIndirectWait(renderGraph) immediately
            // before RenderDeferredLighting. See HDRPPatches/manifest.json :
            // lightlist-indirect-wait-migration.
            m_DeferredNativeLightListIndirectWait = new NativeLightListIndirectWaitState
            {
                valid = true,
                output = output,
                waitCallback = waitCallback,
                enableFeatureVariants = enableFeatureVariants,
                parityFptl = parityFptl,
                parityLightList = parityLightList,
                parityTileFeatureFlags = parityTileFeatureFlags,
                parityCompareDwords = parityCompareDwords,
                parityClustered = parityClustered,
                nativeClustered = nativeClustered,
                parityClusteredLightList = parityClusteredLightList,
                parityClusteredOffset = parityClusteredOffset,
                parityClusteredOffsetDwords = parityClusteredOffsetDwords,
                parityClusteredListDwords = parityClusteredListDwords,
                numTiles = numTiles,
                numTilesX = numTilesX,
                clusterNumTilesX = clusterNumTilesX,
                clusterNumTilesY = clusterNumTilesY,
                viewCount = viewCount,
                totalLightCount = totalLightCount,
                frameIndex = frameIndex,
            };
        }

        // Phase 1c wait-migration: captured at producer-site time, consumed
        // by EmitDeferredNativeLightListIndirectWait() at consumer site.
        struct NativeLightListIndirectWaitState
        {
            public bool valid;
            public BuildGPULightListOutput output;
            public IntPtr waitCallback;
            public bool enableFeatureVariants;
            public bool parityFptl;
            public BufferHandle parityLightList;
            public BufferHandle parityTileFeatureFlags;
            public int parityCompareDwords;
            public bool parityClustered;
            public bool nativeClustered;
            public BufferHandle parityClusteredLightList;
            public BufferHandle parityClusteredOffset;
            public int parityClusteredOffsetDwords;
            public int parityClusteredListDwords;
            public int numTiles;
            public int numTilesX;
            public int clusterNumTilesX;
            public int clusterNumTilesY;
            public int viewCount;
            public int totalLightCount;
            public uint frameIndex;
        }

        NativeLightListIndirectWaitState m_DeferredNativeLightListIndirectWait;

        // Phase 1c wait-migration: consumer-site entry point. Invoke
        // immediately before the first graphics-queue consumer of the
        // LightList Indirect outputs (RenderDeferredLighting today; could be
        // pulled earlier to right before RenderSSR if SSR ever becomes the
        // first consumer). Cross-compute consumers (NativeContactShadows)
        // already get producer→consumer ordering directly via the Phase 1b
        // computeDoneAux semaphore registered under
        // VKComputePassId::NativeLightListIndirect — they DO NOT need this
        // wait pass.
        void EmitDeferredNativeLightListIndirectWait(RenderGraph renderGraph)
        {
            if (!m_DeferredNativeLightListIndirectWait.valid)
                return;
            var s = m_DeferredNativeLightListIndirectWait;
            // Reset before invoking — defensive against re-entry / double-emit.
            m_DeferredNativeLightListIndirectWait = default;
            WaitNativeLightListIndirect(renderGraph, s.output, s.waitCallback, s.enableFeatureVariants, s.parityFptl, s.parityLightList, s.parityTileFeatureFlags, s.parityCompareDwords, s.parityClustered, s.nativeClustered, s.parityClusteredLightList, s.parityClusteredOffset, s.parityClusteredOffsetDwords, s.parityClusteredListDwords, s.numTiles, s.numTilesX, s.clusterNumTilesX, s.clusterNumTilesY, s.viewCount, s.totalLightCount, s.frameIndex);
        }

        class NativeLightListIndirectWaitPassData
        {
            public BufferHandle tileFeatureFlags;
            public BufferHandle lightList;
            public BufferHandle parityTileFeatureFlags;
            public BufferHandle parityLightList;
            public BufferHandle parityClusteredLightList;
            public BufferHandle parityClusteredOffset;
            public BufferHandle stockClusteredLightList;
            public BufferHandle stockClusteredOffset;
            public BufferHandle stockClusteredLogBase;
            public BufferHandle tileList;
            public BufferHandle dispatchIndirectBuffer;
            public IntPtr waitCallback;
            public bool enableFeatureVariants;
            public bool parityFptl;
            public bool parityClustered;
            public bool nativeClustered;
            public int parityCompareDwords;
            public int parityClusteredOffsetDwords;
            public int parityClusteredListDwords;
            public int numTiles;
            public int numTilesX;
            public int numTilesY;
            public int clusterNumTilesX;
            public int clusterNumTilesY;
            public int viewCount;
            public int totalLightCount;
            public uint frameIndex;
        }

        void WaitNativeLightListIndirect(RenderGraph renderGraph, BuildGPULightListOutput output, IntPtr waitCallback, bool enableFeatureVariants, bool parityFptl, BufferHandle parityLightList, BufferHandle parityTileFeatureFlags, int parityCompareDwords, bool parityClustered, bool nativeClustered, BufferHandle parityClusteredLightList, BufferHandle parityClusteredOffset, int parityClusteredOffsetDwords, int parityClusteredListDwords, int numTiles, int numTilesX, int clusterNumTilesX, int clusterNumTilesY, int viewCount, int totalLightCount, uint frameIndex)
        {
            using (var builder = renderGraph.AddUnsafePass<NativeLightListIndirectWaitPassData>("FSR3 Native LightList Indirect Wait", out var passData, ProfilingSampler.Get(HDProfileId.BuildLightList)))
            {
                passData.tileFeatureFlags = output.tileFeatureFlags;
                passData.lightList = output.lightList;
                passData.parityTileFeatureFlags = parityTileFeatureFlags;
                passData.parityLightList = parityLightList;
                passData.parityClusteredLightList = parityClusteredLightList;
                passData.parityClusteredOffset = parityClusteredOffset;
                passData.stockClusteredLightList = output.perVoxelLightLists;
                passData.stockClusteredOffset = output.perVoxelOffset;
                passData.stockClusteredLogBase = output.perTileLogBaseTweak;
                passData.tileList = output.tileList;
                passData.dispatchIndirectBuffer = output.dispatchIndirectBuffer;
                passData.waitCallback = waitCallback;
                passData.enableFeatureVariants = enableFeatureVariants;
                passData.parityFptl = parityFptl;
                passData.parityClustered = parityClustered;
                passData.nativeClustered = nativeClustered;
                passData.parityCompareDwords = parityCompareDwords;
                passData.parityClusteredOffsetDwords = parityClusteredOffsetDwords;
                passData.parityClusteredListDwords = parityClusteredListDwords;
                passData.numTiles = numTiles;
                passData.numTilesX = numTilesX;
                passData.numTilesY = 0;
                passData.clusterNumTilesX = clusterNumTilesX;
                passData.clusterNumTilesY = clusterNumTilesY;
                passData.viewCount = viewCount;
                passData.totalLightCount = totalLightCount;
                passData.frameIndex = frameIndex;

                builder.UseBuffer(passData.tileFeatureFlags,
                    passData.enableFeatureVariants ? AccessFlags.ReadWrite : AccessFlags.Read);
                if (passData.lightList.IsValid())
                    builder.UseBuffer(passData.lightList, AccessFlags.ReadWrite);
                if (passData.parityFptl)
                {
                    builder.UseBuffer(passData.parityLightList, AccessFlags.ReadWrite);
                    builder.UseBuffer(passData.parityTileFeatureFlags, AccessFlags.ReadWrite);
                }
                if (passData.parityClustered)
                {
                    builder.UseBuffer(passData.stockClusteredOffset, AccessFlags.Read);
                    builder.UseBuffer(passData.stockClusteredLightList, AccessFlags.Read);
                    builder.UseBuffer(passData.parityClusteredOffset, AccessFlags.ReadWrite);
                    builder.UseBuffer(passData.parityClusteredLightList, AccessFlags.ReadWrite);
                }
                if (passData.nativeClustered)
                {
                    builder.UseBuffer(passData.stockClusteredOffset, AccessFlags.ReadWrite);
                    builder.UseBuffer(passData.stockClusteredLightList, AccessFlags.ReadWrite);
                    if (passData.stockClusteredLogBase.IsValid())
                        builder.UseBuffer(passData.stockClusteredLogBase, AccessFlags.ReadWrite);
                }
                if (passData.enableFeatureVariants)
                {
                    builder.UseBuffer(passData.tileList, AccessFlags.ReadWrite);
                    builder.UseBuffer(passData.dispatchIndirectBuffer, AccessFlags.ReadWrite);
                }
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (NativeLightListIndirectWaitPassData data, UnsafeGraphContext ctx) =>
                    {
                        if (data.waitCallback != IntPtr.Zero)
                            ctx.cmd.IssuePluginEvent(data.waitCallback, kNativeLightListIndirectWaitEventId);

                        if (data.parityFptl &&
                            data.parityCompareDwords > 0 &&
                            data.lightList.IsValid() &&
                            data.parityLightList.IsValid() &&
                            (s_NativeLightListFptlParityRequestCounter++ % kNativeLightListFptlParityReadbackInterval) == 0)
                        {
                            GraphicsBuffer stockLightList = data.lightList;
                            GraphicsBuffer nativeLightList = data.parityLightList;
                            uint frame = data.frameIndex;
                            int compareDwords = data.parityCompareDwords;
                            int tiles = data.numTiles;
                            int tilesX = data.numTilesX;
                            int views = data.viewCount;
                            int lights = data.totalLightCount;
                            ctx.cmd.RequestAsyncReadback(stockLightList,
                                request => OnNativeLightListFptlParityReadback(frame, false, compareDwords, tiles, tilesX, views, lights, request));
                            ctx.cmd.RequestAsyncReadback(nativeLightList,
                                request => OnNativeLightListFptlParityReadback(frame, true, compareDwords, tiles, tilesX, views, lights, request));
                        }

                        if (data.parityClustered &&
                            data.parityClusteredOffsetDwords > 0 &&
                            data.parityClusteredListDwords > 0 &&
                            data.stockClusteredOffset.IsValid() &&
                            data.stockClusteredLightList.IsValid() &&
                            data.parityClusteredOffset.IsValid() &&
                            data.parityClusteredLightList.IsValid() &&
                            (s_NativeLightListClusteredParityRequestCounter++ % kNativeLightListClusteredParityReadbackInterval) == 0)
                        {
                            GraphicsBuffer stockOffset = data.stockClusteredOffset;
                            GraphicsBuffer stockLightList = data.stockClusteredLightList;
                            GraphicsBuffer nativeOffset = data.parityClusteredOffset;
                            GraphicsBuffer nativeLightList = data.parityClusteredLightList;
                            uint frame = data.frameIndex;
                            int offsetDwords = data.parityClusteredOffsetDwords;
                            int listDwords = data.parityClusteredListDwords;
                            int tiles = data.clusterNumTilesX * data.clusterNumTilesY;
                            int tilesX = data.clusterNumTilesX;
                            int tilesY = data.clusterNumTilesY;
                            int views = data.viewCount;
                            int clusters = 1 << k_Log2NumClusters;
                            int lights = data.totalLightCount;
                            ctx.cmd.RequestAsyncReadback(stockOffset,
                                request => OnNativeLightListClusteredParityReadback(frame, 0, offsetDwords, listDwords, tiles, tilesX, tilesY, views, clusters, lights, request));
                            ctx.cmd.RequestAsyncReadback(nativeOffset,
                                request => OnNativeLightListClusteredParityReadback(frame, 1, offsetDwords, listDwords, tiles, tilesX, tilesY, views, clusters, lights, request));
                            ctx.cmd.RequestAsyncReadback(stockLightList,
                                request => OnNativeLightListClusteredParityReadback(frame, 2, offsetDwords, listDwords, tiles, tilesX, tilesY, views, clusters, lights, request));
                            ctx.cmd.RequestAsyncReadback(nativeLightList,
                                request => OnNativeLightListClusteredParityReadback(frame, 3, offsetDwords, listDwords, tiles, tilesX, tilesY, views, clusters, lights, request));
                        }
                    });
            }
        }

        class PushGlobalCameraParamPassData
        {
            public ShaderVariablesGlobal globalCB;
            public ShaderVariablesXR xrCB;
        }

        void PushGlobalCameraParams(RenderGraph renderGraph, HDCamera hdCamera)
        {
            using (var builder = renderGraph.AddUnsafePass<PushGlobalCameraParamPassData>("Push Global Camera Parameters", out var passData))
            {
                builder.AllowGlobalStateModification(true);

                passData.globalCB = m_ShaderVariablesGlobalCB;
                passData.xrCB = m_ShaderVariablesXRCB;

                builder.SetRenderFunc(
                    (PushGlobalCameraParamPassData data, UnsafeGraphContext ctx) =>
                    {
                        ConstantBuffer.PushGlobal(ctx.cmd, data.globalCB, HDShaderIDs._ShaderVariablesGlobal);
                        ConstantBuffer.PushGlobal(ctx.cmd, data.xrCB, HDShaderIDs._ShaderVariablesXR);
                    });
            }
        }

        internal ShadowResult RenderShadows(RenderGraph renderGraph, ScriptableRenderContext renderContext, HDCamera hdCamera, CullingResults cullResults, ref ShadowResult result)
        {
            m_ShadowManager.RenderShadows(m_RenderGraph, renderContext, m_ShaderVariablesGlobalCB, hdCamera, cullResults, ref result);
            // Need to restore global camera parameters.
            PushGlobalCameraParams(renderGraph, hdCamera);
            return result;
        }

        TextureHandle CreateDiffuseLightingBuffer(RenderGraph renderGraph, MSAASamples msaaSamples)
        {
            bool msaa = msaaSamples != MSAASamples.None;
            return renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
            {
                format = GraphicsFormat.B10G11R11_UFloatPack32,
                enableRandomWrite = !msaa,
                bindTextureMS = msaa,
                msaaSamples = msaaSamples,
                clearBuffer = true,
                clearColor = Color.clear,
                name = msaa ? "CameraSSSDiffuseLightingMSAA" : "CameraSSSDiffuseLighting"
            });
        }

        class DeferredLightingPassData
        {
            public int numTilesX;
            public int numTilesY;
            public int numTiles;
            public bool outputSplitLighting;
            public bool enableFeatureVariants;
            public bool enableShadowMasks;
            public int numVariants;
            public DebugDisplaySettings debugDisplaySettings;

            // Compute Lighting
            public ComputeShader deferredComputeShader;
            public int viewCount;

            public TextureHandle colorBuffer;
            public TextureHandle sssDiffuseLightingBuffer;
            public TextureHandle depthBuffer;
            public TextureHandle depthTexture;

            public int gbufferCount;
            public int lightLayersTextureIndex;
            public int shadowMaskTextureIndex;
            public TextureHandle[] gbuffer = new TextureHandle[8];

            public BufferHandle lightListBuffer;
            public BufferHandle tileFeatureFlagsBuffer;
            public BufferHandle tileListBuffer;
            public BufferHandle dispatchIndirectBuffer;

            public LightingBuffers lightingBuffers;
        }

        struct LightingOutput
        {
            public TextureHandle colorBuffer;
        }

        static void RenderComputeDeferredLighting(DeferredLightingPassData data, RenderTargetIdentifier[] colorBuffers, CommandBuffer cmd)
        {
            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.RenderDeferredLightingCompute)))
            {
                data.deferredComputeShader.shaderKeywords = null;

                switch (HDRenderPipeline.currentAsset.currentPlatformRenderPipelineSettings.hdShadowInitParams.punctualShadowFilteringQuality)
                {
                    case HDShadowFilteringQuality.Low:
                        data.deferredComputeShader.EnableKeyword("PUNCTUAL_SHADOW_LOW");
                        break;
                    case HDShadowFilteringQuality.Medium:
                        data.deferredComputeShader.EnableKeyword("PUNCTUAL_SHADOW_MEDIUM");
                        break;
                    case HDShadowFilteringQuality.High:
                        data.deferredComputeShader.EnableKeyword("PUNCTUAL_SHADOW_HIGH");
                        break;
                    default:
                        data.deferredComputeShader.EnableKeyword("PUNCTUAL_SHADOW_MEDIUM");
                        break;
                }

                switch (HDRenderPipeline.currentAsset.currentPlatformRenderPipelineSettings.hdShadowInitParams.directionalShadowFilteringQuality)
                {
                    case HDShadowFilteringQuality.Low:
                        data.deferredComputeShader.EnableKeyword("DIRECTIONAL_SHADOW_LOW");
                        break;
                    case HDShadowFilteringQuality.Medium:
                        data.deferredComputeShader.EnableKeyword("DIRECTIONAL_SHADOW_MEDIUM");
                        break;
                    case HDShadowFilteringQuality.High:
                        data.deferredComputeShader.EnableKeyword("DIRECTIONAL_SHADOW_HIGH");
                        break;
                    default:
                        data.deferredComputeShader.EnableKeyword("DIRECTIONAL_SHADOW_MEDIUM");
                        break;
                }

                var areaShadowFilteringQuality = (ShaderConfig.s_AreaLights == 0) ? HDAreaShadowFilteringQuality.Medium
                    : HDRenderPipeline.currentAsset.currentPlatformRenderPipelineSettings.hdShadowInitParams.areaShadowFilteringQuality;
                switch (areaShadowFilteringQuality)
                {
                    case HDAreaShadowFilteringQuality.Medium:
                        data.deferredComputeShader.EnableKeyword("AREA_SHADOW_MEDIUM");
                        break;
                    case HDAreaShadowFilteringQuality.High:
                        data.deferredComputeShader.EnableKeyword("AREA_SHADOW_HIGH");
                        break;
                    default:
                        data.deferredComputeShader.EnableKeyword("AREA_SHADOW_MEDIUM");
                        break;
                }

                if (data.enableShadowMasks)
                {
                    data.deferredComputeShader.EnableKeyword("SHADOWS_SHADOWMASK");
                }

                if (data.enableFeatureVariants)
                {
                    for (int variant = 0; variant < data.numVariants; variant++)
                    {
                        var kernel = s_shadeOpaqueIndirectFptlKernels[variant];

                        cmd.SetComputeTextureParam(data.deferredComputeShader, kernel, HDShaderIDs._CameraDepthTexture, data.depthTexture);

                        // TODO: Is it possible to setup this outside the loop ? Can figure out how, get this: Property (specularLightingUAV) at kernel index (21) is not set
                        cmd.SetComputeTextureParam(data.deferredComputeShader, kernel, HDShaderIDs.specularLightingUAV, colorBuffers[0]);
                        cmd.SetComputeTextureParam(data.deferredComputeShader, kernel, HDShaderIDs.diffuseLightingUAV, colorBuffers[1]);
                        cmd.SetComputeBufferParam(data.deferredComputeShader, kernel, HDShaderIDs.g_vLightListTile, data.lightListBuffer);

                        cmd.SetComputeTextureParam(data.deferredComputeShader, kernel, HDShaderIDs._StencilTexture, data.depthBuffer, 0, RenderTextureSubElement.Stencil);

                        // always do deferred lighting in blocks of 16x16 (not same as tiled light size)
                        cmd.SetComputeBufferParam(data.deferredComputeShader, kernel, HDShaderIDs.g_TileFeatureFlags, data.tileFeatureFlagsBuffer);
                        cmd.SetComputeIntParam(data.deferredComputeShader, HDShaderIDs.g_TileListOffset, variant * data.numTiles * data.viewCount);
                        cmd.SetComputeBufferParam(data.deferredComputeShader, kernel, HDShaderIDs.g_TileList, data.tileListBuffer);
                        cmd.DispatchCompute(data.deferredComputeShader, kernel, data.dispatchIndirectBuffer, (uint)variant * 3 * sizeof(uint));
                    }
                }
                else
                {
                    var kernel = data.debugDisplaySettings.IsDebugDisplayEnabled() ? s_shadeOpaqueDirectFptlDebugDisplayKernel : s_shadeOpaqueDirectFptlKernel;

                    cmd.SetComputeTextureParam(data.deferredComputeShader, kernel, HDShaderIDs._CameraDepthTexture, data.depthTexture);

                    cmd.SetComputeTextureParam(data.deferredComputeShader, kernel, HDShaderIDs.specularLightingUAV, colorBuffers[0]);
                    cmd.SetComputeTextureParam(data.deferredComputeShader, kernel, HDShaderIDs.diffuseLightingUAV, colorBuffers[1]);
                    cmd.SetComputeBufferParam(data.deferredComputeShader, kernel, HDShaderIDs.g_vLightListTile, data.lightListBuffer);

                    cmd.SetComputeTextureParam(data.deferredComputeShader, kernel, HDShaderIDs._StencilTexture, data.depthBuffer, 0, RenderTextureSubElement.Stencil);

                    // 4x 8x8 groups per a 16x16 tile.
                    cmd.DispatchCompute(data.deferredComputeShader, kernel, data.numTilesX * 2, data.numTilesY * 2, data.viewCount);
                }

            }
        }

        LightingOutput RenderDeferredLighting(
            RenderGraph renderGraph,
            HDCamera hdCamera,
            TextureHandle colorBuffer,
            TextureHandle depthStencilBuffer,
            TextureHandle depthPyramidTexture,
            in LightingBuffers lightingBuffers,
            in GBufferOutput gbuffer,
            in ShadowResult shadowResult,
            in BuildGPULightListOutput lightLists)
        {
            if (hdCamera.frameSettings.litShaderMode != LitShaderMode.Deferred ||
                !hdCamera.frameSettings.IsEnabled(FrameSettingsField.OpaqueObjects))
                return new LightingOutput();

            using (var builder = renderGraph.AddUnsafePass<DeferredLightingPassData>("Deferred Lighting", out var passData))
            {
                bool debugDisplayOrSceneLightOff = CoreUtils.IsSceneLightingDisabled(hdCamera.camera) || m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled();

                int w = hdCamera.actualWidth;
                int h = hdCamera.actualHeight;
                passData.numTilesX = (w + 15) / 16;
                passData.numTilesY = (h + 15) / 16;
                passData.numTiles = passData.numTilesX * passData.numTilesY;
                passData.outputSplitLighting = hdCamera.frameSettings.IsEnabled(FrameSettingsField.SubsurfaceScattering);
                passData.enableFeatureVariants = GetFeatureVariantsEnabled(hdCamera.frameSettings) && !debugDisplayOrSceneLightOff;
                passData.enableShadowMasks = m_EnableBakeShadowMask;
                passData.numVariants = LightDefinitions.s_NumFeatureVariants;
                passData.debugDisplaySettings = m_CurrentDebugDisplaySettings;

                // Compute Lighting
                passData.deferredComputeShader = deferredComputeShader;
                passData.viewCount = hdCamera.viewCount;

                passData.colorBuffer = colorBuffer;
                builder.UseTexture(passData.colorBuffer, AccessFlags.Write);
                if (passData.outputSplitLighting)
                {
                    passData.sssDiffuseLightingBuffer = lightingBuffers.diffuseLightingBuffer;
                    builder.UseTexture(passData.sssDiffuseLightingBuffer, AccessFlags.Write);
                }
                else
                {
                    // TODO RENDERGRAPH: Check how to avoid this kind of pattern.
                    // Unfortunately, the low level needs this texture to always be bound with UAV enabled, so in order to avoid effectively creating the full resolution texture here,
                    // we need to create a small dummy texture.
                    passData.sssDiffuseLightingBuffer = builder.CreateTransientTexture(new TextureDesc(1, 1, true, true) { format = GraphicsFormat.B10G11R11_UFloatPack32, enableRandomWrite = true });
                }
                passData.depthBuffer = depthStencilBuffer;
                builder.UseTexture(passData.depthBuffer, AccessFlags.Read);
                passData.depthTexture = depthPyramidTexture;
                builder.UseTexture(passData.depthTexture, AccessFlags.Read);

                passData.lightingBuffers = ReadLightingBuffers(lightingBuffers, builder);

                passData.lightLayersTextureIndex = gbuffer.lightLayersTextureIndex;
                passData.shadowMaskTextureIndex = gbuffer.shadowMaskTextureIndex;
                passData.gbufferCount = gbuffer.gBufferCount;
                for (int i = 0; i < gbuffer.gBufferCount; ++i)
                {
                    passData.gbuffer[i] = gbuffer.mrt[i];
                    builder.UseTexture(passData.gbuffer[i], AccessFlags.Read);
                }

                HDShadowManager.ReadShadowResult(shadowResult, builder);

                passData.lightListBuffer = lightLists.lightList;
                builder.UseBuffer(passData.lightListBuffer, AccessFlags.Read);
                passData.tileFeatureFlagsBuffer = lightLists.tileFeatureFlags;
                builder.UseBuffer(passData.tileFeatureFlagsBuffer, AccessFlags.Read);
                passData.tileListBuffer = lightLists.tileList;
                builder.UseBuffer(passData.tileListBuffer, AccessFlags.Read);
                passData.dispatchIndirectBuffer = lightLists.dispatchIndirectBuffer;
                builder.UseBuffer(passData.dispatchIndirectBuffer, AccessFlags.Read);

                builder.AllowGlobalStateModification(true);

                var output = new LightingOutput();
                output.colorBuffer = passData.colorBuffer;

                builder.SetRenderFunc(
                    (DeferredLightingPassData data, UnsafeGraphContext ctx) =>
                    {
                        var natCmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                        var colorBuffers = ctx.renderGraphPool.GetTempArray<RenderTargetIdentifier>(2);
                        colorBuffers[0] = data.colorBuffer;
                        colorBuffers[1] = data.sssDiffuseLightingBuffer;

                        // TODO RENDERGRAPH: Remove these SetGlobal and properly send these textures to the deferred passes and bind them directly to compute shaders.
                        // This can wait that we remove the old code path.
                        for (int i = 0; i < data.gbufferCount; ++i)
                            natCmd.SetGlobalTexture(HDShaderIDs._GBufferTexture[i], data.gbuffer[i]);

                        if (data.lightLayersTextureIndex != -1)
                            natCmd.SetGlobalTexture(HDShaderIDs._RenderingLayersTexture, data.gbuffer[data.lightLayersTextureIndex]);
                        else
                            natCmd.SetGlobalTexture(HDShaderIDs._RenderingLayersTexture, TextureXR.GetWhiteTexture());

                        if (data.shadowMaskTextureIndex != -1)
                            natCmd.SetGlobalTexture(HDShaderIDs._ShadowMaskTexture, data.gbuffer[data.shadowMaskTextureIndex]);
                        else
                            natCmd.SetGlobalTexture(HDShaderIDs._ShadowMaskTexture, TextureXR.GetWhiteTexture());

                        BindGlobalLightingBuffers(data.lightingBuffers, natCmd);
                        RenderComputeDeferredLighting(data, colorBuffers, natCmd);
                    });

                return output;
            }
        }

        class RenderSSRPassData
        {
            public ComputeShader ssrCS;
            public int tracingKernel;
            public int reprojectionKernel;
            public int accumulateNoWorldSpeedRejectionBothKernel;
            public int accumulateNoWorldSpeedRejectionSurfaceKernel;
            public int accumulateNoWorldSpeedRejectionHitKernel;
            public int accumulateHardThresholdSpeedRejectionBothKernel;
            public int accumulateHardThresholdSpeedRejectionSurfaceKernel;
            public int accumulateHardThresholdSpeedRejectionHitKernel;
            public int accumulateSmoothSpeedRejectionBothKernel;
            public int accumulateSmoothSpeedRejectionSurfaceKernel;
            public int accumulateSmoothSpeedRejectionHitKernel;

            public int accumulateNoWorldSpeedRejectionBothDebugKernel;
            public int accumulateNoWorldSpeedRejectionSurfaceDebugKernel;
            public int accumulateNoWorldSpeedRejectionHitDebugKernel;
            public int accumulateHardThresholdSpeedRejectionBothDebugKernel;
            public int accumulateHardThresholdSpeedRejectionSurfaceDebugKernel;
            public int accumulateHardThresholdSpeedRejectionHitDebugKernel;
            public int accumulateSmoothSpeedRejectionBothDebugKernel;
            public int accumulateSmoothSpeedRejectionSurfaceDebugKernel;
            public int accumulateSmoothSpeedRejectionHitDebugKernel;

            public bool transparentSSR;
            public bool usePBRAlgo;
            public bool previousAccumNeedClear;
            public bool validColorPyramid;

            public int width, height, viewCount;

            public ComputeBuffer offsetBufferData;

            public ShaderVariablesScreenSpaceReflection cb;

            public TextureHandle depthBuffer;
            public TextureHandle depthPyramid;
            public TextureHandle normalBuffer;
            public TextureHandle motionVectorsBuffer;
            public TextureHandle colorPyramid;
            public TextureHandle stencilBuffer;
            public TextureHandle hitPointsTexture;
            public TextureHandle ssrAccum;
            public TextureHandle ssrAccumPrev;
            public TextureHandle clearCoatMask;

            public BufferHandle coarseStencilBuffer;

            public BlueNoise.DitheredTextureHandleSet blueNoiseResources;
            public HDCamera hdCamera;

            public ComputeShader clearBuffer2DCS;
            public int clearBuffer2DKernel;

            public bool useAsync;

            public float frameIndex;
            public float roughnessBiasFactor;
            public float speedRejection;
            public float speedRejectionFactor;

            public bool debugDisplaySpeed;
            public bool enableWorldSmoothRejection;
            public bool smoothSpeedRejection;
            public bool motionVectorFromSurface;
            public bool motionVectorFromHit;
        }

        static void ClearColorBuffer2D(RenderSSRPassData data, ComputeCommandBuffer cmd, TextureHandle rt, Color clearColor, bool async)
        {
            if (!async)
            {
                CoreUtils.SetRenderTarget(cmd, rt, ClearFlag.Color, clearColor);
            }
            else
            {
                cmd.SetComputeTextureParam(data.clearBuffer2DCS, data.clearBuffer2DKernel, HDShaderIDs._Buffer2D, rt);
                cmd.SetComputeVectorParam(data.clearBuffer2DCS, HDShaderIDs._ClearValue, clearColor);
                cmd.SetComputeVectorParam(data.clearBuffer2DCS, HDShaderIDs._BufferSize, new Vector4((float)data.width, (float)data.height, 0.0f, 0.0f));
                cmd.DispatchCompute(data.clearBuffer2DCS, data.clearBuffer2DKernel, HDUtils.DivRoundUp(data.width, 8), HDUtils.DivRoundUp(data.height, 8), data.viewCount);
            }
        }

        void UpdateSSRConstantBuffer(HDCamera hdCamera, ScreenSpaceReflection settings, bool isTransparent, ref ShaderVariablesScreenSpaceReflection cb)
        {
            float n = hdCamera.camera.nearClipPlane;
            float f = hdCamera.camera.farClipPlane;
            float thickness = settings.depthBufferThickness.value;

            cb._SsrThicknessScale = 1.0f / (1.0f + thickness);
            cb._SsrThicknessBias = -n / (f - n) * (thickness * cb._SsrThicknessScale);
            cb._SsrIterLimit = settings.rayMaxIterations;
            // We disable sky reflection for transparent in case of a scenario where a transparent object seeing the sky through it is visible in the reflection.
            // As it has no depth it will appear extremely distorted (depth at infinity). This scenario happen frequently when you have transparent objects above water.
            // Note that the sky is still visible, it just takes its value from reflection probe/skybox rather than on screen.
            cb._SsrReflectsSky = isTransparent ? 0 : (settings.reflectSky.value ? 1 : 0);
            cb._SsrStencilBit = (int)StencilUsage.TraceReflectionRay;
            float roughnessFadeStart = 1 - settings.smoothnessFadeStart;
            cb._SsrRoughnessFadeEnd = 1 - settings.minSmoothness;
            float roughnessFadeLength = cb._SsrRoughnessFadeEnd - roughnessFadeStart;
            cb._SsrRoughnessFadeEndTimesRcpLength = (roughnessFadeLength != 0) ? (cb._SsrRoughnessFadeEnd * (1.0f / roughnessFadeLength)) : 1;
            cb._SsrRoughnessFadeRcpLength = (roughnessFadeLength != 0) ? (1.0f / roughnessFadeLength) : 0;
            cb._SsrEdgeFadeRcpLength = Mathf.Min(1.0f / settings.screenFadeDistance.value, float.MaxValue);
            cb._SsrColorPyramidMaxMip = hdCamera.colorPyramidHistoryMipCount - 1;
            cb._SsrDepthPyramidMaxMip = hdCamera.depthBufferMipChainInfo.mipLevelCount - 1;
            if (hdCamera.isFirstFrame || hdCamera.cameraFrameCount <= 3)
            {
                cb._SsrAccumulationAmount = 1.0f;
            }
            else
            {
                cb._SsrAccumulationAmount = Mathf.Pow(2, Mathf.Lerp(0.0f, -7.0f, settings.accumulationFactor.value));
            }

            if (settings.enableWorldSpeedRejection.value && !settings.speedSmoothReject.value)
                cb._SsrPBRSpeedRejection = Mathf.Clamp01(1.0f - settings.speedRejectionParam.value);
            else
                cb._SsrPBRSpeedRejection = Mathf.Clamp01(settings.speedRejectionParam.value);
            cb._SsrPBRBias = settings.biasFactor.value;
            cb._SsrPRBSpeedRejectionScalerFactor = Mathf.Pow(settings.speedRejectionScalerFactor.value * 0.1f, 2.0f);
        }

        TextureHandle RenderSSR(RenderGraph renderGraph,
            HDCamera hdCamera,
            ref PrepassOutput prepassOutput,
            TextureHandle clearCoatMask,
            TextureHandle rayCountTexture,
            TextureHandle historyValidationTexture,
            Texture skyTexture,
            bool transparent)
        {
            if (!hdCamera.IsSSREnabled(transparent))
                return renderGraph.defaultResources.blackTextureXR;

            TextureHandle result;

            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();
            if (EnableRayTracedReflections(hdCamera, settings))
            {
                result = RenderRayTracedReflections(renderGraph, hdCamera,
                    prepassOutput, clearCoatMask, skyTexture, rayCountTexture, historyValidationTexture,
                    m_ShaderVariablesRayTracingCB, transparent);
            }
            else
            {
                if (transparent)
                {
                    // NOTE: Currently we profiled that generating the HTile for SSR and using it is not worth it the optimization.
                    // However if the generated HTile will be used for something else but SSR, this should be made NOT resolve only and
                    // re-enabled in the shader.
                    BuildCoarseStencilAndResolveIfNeeded(renderGraph, hdCamera, resolveOnly: true, ref prepassOutput);
                }

                // The first color pyramid of the frame is generated after the SSR transparent, so we have no choice but to use the previous
                // frame color pyramid (that includes transparents from the previous frame).
                RTHandle colorPyramidRT = hdCamera.GetPreviousFrameRT((int)HDCameraFrameHistoryType.ColorBufferMipChain);
                if (colorPyramidRT == null)
                    return renderGraph.defaultResources.blackTextureXR;

                var blueNoise = GetBlueNoiseManager();
                var blueNoiseResources = BlueNoise.ImportSetToRenderGraph(renderGraph, blueNoise.DitheredTextureSet1SPP());

                bool usePBRAlgo = !transparent && settings.usedAlgorithm.value == ScreenSpaceReflectionAlgorithm.PBRAccumulation;
                TextureHandle ssrAccum = TextureHandle.nullHandle;
                TextureHandle ssrAccumPrev = TextureHandle.nullHandle;

                using (var builder = renderGraph.AddComputePass<RenderSSRPassData>("Render SSR", out var passData))
                {
                    // We disable async for transparent SSR as it would cause direct sync to the graphics pipe and would compete with other heavy passes for GPU resource.
                    bool useAsync = hdCamera.frameSettings.SSRRunsAsync() && !transparent;
                    builder.EnableAsyncCompute(useAsync);

                    var colorPyramid = renderGraph.ImportTexture(colorPyramidRT);
                    var volumeSettings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();

                    if (usePBRAlgo)
                        hdCamera.AllocateScreenSpaceAccumulationHistoryBuffer(1.0f);

                    UpdateSSRConstantBuffer(hdCamera, volumeSettings, transparent, ref passData.cb);

                    passData.hdCamera = hdCamera;
                    passData.ssrCS = m_ScreenSpaceReflectionsCS;
                    passData.tracingKernel = m_SsrTracingKernel;
                    passData.reprojectionKernel = m_SsrReprojectionKernel;
                    passData.accumulateNoWorldSpeedRejectionBothKernel = m_SsrAccumulateNoWorldSpeedRejectionBothKernel;
                    passData.accumulateNoWorldSpeedRejectionSurfaceKernel = m_SsrAccumulateNoWorldSpeedRejectionSurfaceKernel;
                    passData.accumulateNoWorldSpeedRejectionHitKernel = m_SsrAccumulateNoWorldSpeedRejectionHitKernel;
                    passData.accumulateHardThresholdSpeedRejectionBothKernel = m_SsrAccumulateHardThresholdSpeedRejectionBothKernel;
                    passData.accumulateHardThresholdSpeedRejectionSurfaceKernel = m_SsrAccumulateHardThresholdSpeedRejectionSurfaceKernel;
                    passData.accumulateHardThresholdSpeedRejectionHitKernel = m_SsrAccumulateHardThresholdSpeedRejectionHitKernel;
                    passData.accumulateSmoothSpeedRejectionBothKernel = m_SsrAccumulateSmoothSpeedRejectionBothKernel;
                    passData.accumulateSmoothSpeedRejectionSurfaceKernel = m_SsrAccumulateSmoothSpeedRejectionSurfaceKernel;
                    passData.accumulateSmoothSpeedRejectionHitKernel = m_SsrAccumulateSmoothSpeedRejectionHitKernel;

                    passData.accumulateNoWorldSpeedRejectionBothDebugKernel = m_SsrAccumulateNoWorldSpeedRejectionBothDebugKernel;
                    passData.accumulateNoWorldSpeedRejectionSurfaceDebugKernel = m_SsrAccumulateNoWorldSpeedRejectionSurfaceDebugKernel;
                    passData.accumulateNoWorldSpeedRejectionHitDebugKernel = m_SsrAccumulateNoWorldSpeedRejectionHitDebugKernel;
                    passData.accumulateHardThresholdSpeedRejectionBothDebugKernel = m_SsrAccumulateHardThresholdSpeedRejectionBothDebugKernel;
                    passData.accumulateHardThresholdSpeedRejectionSurfaceDebugKernel = m_SsrAccumulateHardThresholdSpeedRejectionSurfaceDebugKernel;
                    passData.accumulateHardThresholdSpeedRejectionHitDebugKernel = m_SsrAccumulateHardThresholdSpeedRejectionHitDebugKernel;
                    passData.accumulateSmoothSpeedRejectionBothDebugKernel = m_SsrAccumulateSmoothSpeedRejectionBothDebugKernel;
                    passData.accumulateSmoothSpeedRejectionSurfaceDebugKernel = m_SsrAccumulateSmoothSpeedRejectionSurfaceDebugKernel;
                    passData.accumulateSmoothSpeedRejectionHitDebugKernel = m_SsrAccumulateSmoothSpeedRejectionHitDebugKernel;

                    passData.transparentSSR = transparent;
                    passData.usePBRAlgo = usePBRAlgo;
                    passData.width = hdCamera.actualWidth;
                    passData.height = hdCamera.actualHeight;
                    passData.viewCount = hdCamera.viewCount;
                    passData.offsetBufferData = hdCamera.depthBufferMipChainInfo.GetOffsetBufferData(m_DepthPyramidMipLevelOffsetsBuffer);
                    passData.previousAccumNeedClear = usePBRAlgo && (hdCamera.isFirstFrame || hdCamera.resetPostProcessingHistory);
                    hdCamera.currentSSRAlgorithm = volumeSettings.usedAlgorithm.value; // Store for next frame comparison
                    passData.validColorPyramid = hdCamera.colorPyramidHistoryValidFrames > 1;

                    LogNativeAsyncCandidate(transparent ? "TransparentSSR" : "SSR",
                        $"res={passData.width}x{passData.height} pbr={usePBRAlgo} asyncRequested={useAsync} validColorPyramid={passData.validColorPyramid} views={passData.viewCount}");

                    passData.blueNoiseResources = blueNoiseResources;
                    builder.UseTexture(passData.blueNoiseResources.owenScrambled256Tex, AccessFlags.Read);
                    builder.UseTexture(passData.blueNoiseResources.rankingTile, AccessFlags.Read);
                    builder.UseTexture(passData.blueNoiseResources.scramblingTile, AccessFlags.Read);
                    builder.UseTexture(passData.blueNoiseResources.scramblingTex, AccessFlags.Read);

                    passData.depthBuffer = prepassOutput.depthBuffer;
                    builder.UseTexture(passData.depthBuffer, AccessFlags.Read);
                    passData.depthPyramid = prepassOutput.depthPyramidTexture;
                    builder.UseTexture(passData.depthPyramid, AccessFlags.Read);
                    passData.colorPyramid = colorPyramid;
                    builder.UseTexture(passData.colorPyramid, AccessFlags.Read);
                    passData.stencilBuffer = prepassOutput.stencilBuffer;
                    builder.UseTexture(passData.stencilBuffer, AccessFlags.Read);
                    passData.clearCoatMask = clearCoatMask;
                    builder.UseTexture(passData.clearCoatMask, AccessFlags.Read);
                    passData.normalBuffer = prepassOutput.resolvedNormalBuffer;
                    builder.UseTexture(passData.normalBuffer, AccessFlags.Read);
                    passData.motionVectorsBuffer = prepassOutput.resolvedMotionVectorsBuffer;
                    builder.UseTexture(passData.motionVectorsBuffer, AccessFlags.Read);
                    if (hdCamera.isFirstFrame || hdCamera.cameraFrameCount <= 2)
                    {
                        passData.frameIndex = 1.0f;
                    }
                    else
                    {
                        passData.frameIndex = ((float)hdCamera.cameraFrameCount);
                    }
                    passData.roughnessBiasFactor = volumeSettings.biasFactor.value;
                    passData.debugDisplaySpeed = m_CurrentDebugDisplaySettings.data.fullScreenDebugMode == FullScreenDebugMode.ScreenSpaceReflectionSpeedRejection;
                    passData.speedRejection = volumeSettings.speedRejectionParam.value;
                    passData.speedRejectionFactor = volumeSettings.speedRejectionScalerFactor.value;
                    passData.enableWorldSmoothRejection = volumeSettings.enableWorldSpeedRejection.value;
                    passData.smoothSpeedRejection = volumeSettings.speedSmoothReject.value;
                    passData.motionVectorFromSurface = volumeSettings.speedSurfaceOnly.value;
                    passData.motionVectorFromHit = volumeSettings.speedTargetOnly.value;

                    passData.clearBuffer2DCS = m_ClearBuffer2DCS;
                    passData.clearBuffer2DKernel = m_ClearBuffer2DKernel;
                    passData.useAsync = useAsync;

                    // In practice, these textures are sparse (mostly black). Therefore, clearing them is fast (due to CMASK),
                    // and much faster than fully overwriting them from within SSR shaders.
                    passData.hitPointsTexture = builder.CreateTransientTexture(new TextureDesc(Vector2.one, true, true)
                    { format = GraphicsFormat.R16G16_UNorm, clearBuffer = !useAsync, clearColor = Color.clear, enableRandomWrite = true, name = transparent ? "SSR_Hit_Point_Texture_Trans" : "SSR_Hit_Point_Texture" });

                    if (usePBRAlgo)
                    {
                        ssrAccum = passData.ssrAccum = renderGraph.ImportTexture(hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.ScreenSpaceReflectionAccumulation));
                        builder.UseTexture(passData.ssrAccum, AccessFlags.Write);
                        ssrAccumPrev = passData.ssrAccumPrev = renderGraph.ImportTexture(hdCamera.GetPreviousFrameRT((int)HDCameraFrameHistoryType.ScreenSpaceReflectionAccumulation));
                        builder.UseTexture(passData.ssrAccumPrev, AccessFlags.Write);
                    }
                    else
                    {
                        passData.ssrAccum = renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
                        { format = GraphicsFormat.R16G16B16A16_SFloat, clearBuffer = !useAsync, clearColor = Color.clear, enableRandomWrite = true, name = "SSR_Lighting_Texture" });
                        builder.UseTexture(passData.ssrAccum, AccessFlags.Write);
                    }

                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc(
                        (RenderSSRPassData data, ComputeGraphContext ctx) =>
                        {
                            var cs = data.ssrCS;
                            ConstantBuffer.Push(ctx.cmd, data.cb, cs, HDShaderIDs._ShaderVariablesScreenSpaceReflection);

                            BlueNoise.BindDitheredTextureSet(ctx.cmd, data.blueNoiseResources);

                            CoreUtils.SetKeyword(ctx.cmd, "SSR_APPROX", !data.usePBRAlgo);
                            CoreUtils.SetKeyword(ctx.cmd, "DEPTH_SOURCE_NOT_FROM_MIP_CHAIN", data.transparentSSR);

                            if (data.usePBRAlgo || data.useAsync)
                            {
                                // When non pbr and not async, clear is done when the accumulation texture is created
                                ClearColorBuffer2D(data, ctx.cmd, data.ssrAccum, Color.clear, data.useAsync);
                            }

                            if (data.usePBRAlgo && (data.previousAccumNeedClear || data.debugDisplaySpeed))
                            {
                                ClearColorBuffer2D(data, ctx.cmd, data.ssrAccumPrev, Color.clear, data.useAsync);
                            }

                            if (data.useAsync)
                            {
                                ClearColorBuffer2D(data, ctx.cmd, data.hitPointsTexture, Color.clear, data.useAsync);
                            }

                            using (new ProfilingScope(ctx.cmd, ProfilingSampler.Get(HDProfileId.SsrTracing)))
                            {
                                // cmd.SetComputeTextureParam(cs, kernel, "_SsrDebugTexture",    m_SsrDebugTexture);
                                // Bind the non mip chain if we are rendering the transparent version
                                ctx.cmd.SetComputeTextureParam(cs, data.tracingKernel, HDShaderIDs._DepthTexture, data.depthBuffer);
                                ctx.cmd.SetComputeTextureParam(cs, data.tracingKernel, HDShaderIDs._CameraDepthTexture, data.depthPyramid);
                                ctx.cmd.SetComputeTextureParam(cs, data.tracingKernel, HDShaderIDs._NormalBufferTexture, data.normalBuffer);
                                ctx.cmd.SetComputeTextureParam(cs, data.tracingKernel, HDShaderIDs._SsrClearCoatMaskTexture, data.clearCoatMask);
                                ctx.cmd.SetComputeTextureParam(cs, data.tracingKernel, HDShaderIDs._SsrHitPointTexture, data.hitPointsTexture);

                                RTHandle stencilBuffer = data.stencilBuffer;
                                if (stencilBuffer.rt.stencilFormat == GraphicsFormat.None)  // We are accessing MSAA resolved version and not the depth stencil buffer directly.
                                    ctx.cmd.SetComputeTextureParam(cs, data.tracingKernel, HDShaderIDs._StencilTexture, data.stencilBuffer);
                                else
                                    ctx.cmd.SetComputeTextureParam(cs, data.tracingKernel, HDShaderIDs._StencilTexture, data.stencilBuffer, 0, RenderTextureSubElement.Stencil);

                                //ctx.cmd.SetComputeBufferParam(cs, data.tracingKernel, HDShaderIDs._CoarseStencilBuffer, data.coarseStencilBuffer);
                                ctx.cmd.SetComputeBufferParam(cs, data.tracingKernel, HDShaderIDs._DepthPyramidMipLevelOffsets, data.offsetBufferData);


                                ctx.cmd.DispatchCompute(cs, data.tracingKernel, HDUtils.DivRoundUp(data.width, 8), HDUtils.DivRoundUp(data.height, 8), data.viewCount);
                            }

                            using (new ProfilingScope(ctx.cmd, ProfilingSampler.Get(HDProfileId.SsrReprojection)))
                            {
                                ctx.cmd.SetComputeTextureParam(cs, data.reprojectionKernel, HDShaderIDs._DepthTexture, data.depthBuffer);
                                ctx.cmd.SetComputeTextureParam(cs, data.reprojectionKernel, HDShaderIDs._CameraDepthTexture, data.depthPyramid);
                                ctx.cmd.SetComputeTextureParam(cs, data.reprojectionKernel, HDShaderIDs._ColorPyramidTexture, data.colorPyramid);
                                ctx.cmd.SetComputeTextureParam(cs, data.reprojectionKernel, HDShaderIDs._NormalBufferTexture, data.normalBuffer);
                                ctx.cmd.SetComputeTextureParam(cs, data.reprojectionKernel, HDShaderIDs._SsrHitPointTexture, data.hitPointsTexture);
                                ctx.cmd.SetComputeTextureParam(cs, data.reprojectionKernel, HDShaderIDs._SSRAccumTexture, data.ssrAccum);
                                ctx.cmd.SetComputeTextureParam(cs, data.reprojectionKernel, HDShaderIDs._SsrClearCoatMaskTexture, data.clearCoatMask);
                                ctx.cmd.SetComputeTextureParam(cs, data.reprojectionKernel, HDShaderIDs._CameraMotionVectorsTexture, data.motionVectorsBuffer);

                                ctx.cmd.DispatchCompute(cs, data.reprojectionKernel, HDUtils.DivRoundUp(data.width, 8), HDUtils.DivRoundUp(data.height, 8), data.viewCount);
                            }

                            if (data.usePBRAlgo)
                            {
                                if (!data.validColorPyramid)
                                {
                                    ClearColorBuffer2D(data, ctx.cmd, data.ssrAccum, Color.clear, data.useAsync);
                                    ClearColorBuffer2D(data, ctx.cmd, data.ssrAccumPrev, Color.clear, data.useAsync);
                                }
                                else
                                {
                                    using (new ProfilingScope(ctx.cmd, ProfilingSampler.Get(HDProfileId.SsrAccumulate)))
                                    {
                                        int pass;
                                        if (data.debugDisplaySpeed)
                                        {
                                            if (!data.enableWorldSmoothRejection)
                                            {
                                                if (data.motionVectorFromSurface && data.motionVectorFromHit)
                                                    pass = data.accumulateNoWorldSpeedRejectionBothDebugKernel;
                                                else if (data.motionVectorFromHit)
                                                    pass = data.accumulateNoWorldSpeedRejectionHitDebugKernel;
                                                else
                                                    pass = data.accumulateNoWorldSpeedRejectionSurfaceDebugKernel;
                                            }
                                            else
                                            {
                                                if (data.smoothSpeedRejection)
                                                {
                                                    if (data.motionVectorFromSurface && data.motionVectorFromHit)
                                                        pass = data.accumulateSmoothSpeedRejectionBothDebugKernel;
                                                    else if (data.motionVectorFromHit)
                                                        pass = data.accumulateSmoothSpeedRejectionHitDebugKernel;
                                                    else
                                                        pass = data.accumulateSmoothSpeedRejectionSurfaceDebugKernel;
                                                }
                                                else
                                                {
                                                    if (data.motionVectorFromSurface && data.motionVectorFromHit)
                                                        pass = data.accumulateHardThresholdSpeedRejectionBothDebugKernel;
                                                    else if (data.motionVectorFromHit)
                                                        pass = data.accumulateHardThresholdSpeedRejectionHitDebugKernel;
                                                    else
                                                        pass = data.accumulateHardThresholdSpeedRejectionSurfaceDebugKernel;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (!data.enableWorldSmoothRejection)
                                            {
                                                if (data.motionVectorFromSurface && data.motionVectorFromHit)
                                                    pass = data.accumulateNoWorldSpeedRejectionBothKernel;
                                                else if (data.motionVectorFromHit)
                                                    pass = data.accumulateNoWorldSpeedRejectionHitKernel;
                                                else
                                                    pass = data.accumulateNoWorldSpeedRejectionSurfaceKernel;
                                            }
                                            else
                                            {
                                                if (data.smoothSpeedRejection)
                                                {
                                                    if (data.motionVectorFromSurface && data.motionVectorFromHit)
                                                        pass = data.accumulateSmoothSpeedRejectionBothKernel;
                                                    else if (data.motionVectorFromHit)
                                                        pass = data.accumulateSmoothSpeedRejectionHitKernel;
                                                    else
                                                        pass = data.accumulateSmoothSpeedRejectionSurfaceKernel;
                                                }
                                                else
                                                {
                                                    if (data.motionVectorFromSurface && data.motionVectorFromHit)
                                                        pass = data.accumulateHardThresholdSpeedRejectionBothKernel;
                                                    else if (data.motionVectorFromHit)
                                                        pass = data.accumulateHardThresholdSpeedRejectionHitKernel;
                                                    else
                                                        pass = data.accumulateHardThresholdSpeedRejectionSurfaceKernel;
                                                }
                                            }
                                        }

                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._DepthTexture, data.depthBuffer);
                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._CameraDepthTexture, data.depthPyramid);
                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._NormalBufferTexture, data.normalBuffer);
                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._ColorPyramidTexture, data.colorPyramid);
                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._SsrHitPointTexture, data.hitPointsTexture);
                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._SSRAccumTexture, data.ssrAccum);
                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._SsrAccumPrev, data.ssrAccumPrev);
                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._SsrClearCoatMaskTexture, data.clearCoatMask);
                                        ctx.cmd.SetComputeTextureParam(cs, pass, HDShaderIDs._CameraMotionVectorsTexture, data.motionVectorsBuffer);

                                        ctx.cmd.DispatchCompute(cs, pass, HDUtils.DivRoundUp(data.width, 8), HDUtils.DivRoundUp(data.height, 8), data.viewCount);
                                    }
                                }
                            }
                        });

                    result = passData.ssrAccum;

                }

                if (usePBRAlgo)
                {
                    PushFullScreenDebugTexture(renderGraph, ssrAccum, FullScreenDebugMode.ScreenSpaceReflectionsAccum);
                    PushFullScreenDebugTexture(renderGraph, ssrAccumPrev, FullScreenDebugMode.ScreenSpaceReflectionsPrev);
                }

                if (ssrAccum.IsValid())
                    PushFullScreenDebugTexture(renderGraph, ssrAccum, FullScreenDebugMode.ScreenSpaceReflectionSpeedRejection);

                if (!hdCamera.colorPyramidHistoryIsValid)
                {
                    result = renderGraph.defaultResources.blackTextureXR;
                }
            }

            PushFullScreenDebugTexture(renderGraph, result, transparent ? FullScreenDebugMode.TransparentScreenSpaceReflections : FullScreenDebugMode.ScreenSpaceReflections);

            return result;
        }

        class RenderContactShadowPassData
        {
            public ComputeShader contactShadowsCS;
            public int kernel;

            public Vector4 params1;
            public Vector4 params2;
            public Vector4 params3;

            public int numTilesX;
            public int numTilesY;
            public int viewCount;

            public bool rayTracingEnabled;
            public RayTracingShader contactShadowsRTS;
            public RayTracingAccelerationStructure accelerationStructure;
            public int actualWidth;
            public int actualHeight;
            public int depthTextureParameterName;

            public LightLoopLightData lightLoopLightData;
            public TextureHandle depthTexture;
            public TextureHandle contactShadowsTexture;
            public BufferHandle lightList;
        }

        class NativeContactShadowsPassData
        {
            public TextureHandle depthTexture;
            public TextureHandle contactShadowsTexture;
            public BufferHandle lightList;
            public LightLoopLightData lightLoopLightData;
            public Vector4 params1;
            public Vector4 params2;
            public Vector4 params3;
            public Vector4 screenSize;
            public Matrix4x4 invViewProj;
            public Matrix4x4 view;
            public Matrix4x4 viewProj;
            public Matrix4x4 proj;
            public int actualWidth;
            public int actualHeight;
            public int numTilesX;
            public int numTilesY;
            public int viewCount;
            public int directionalLightCount;
            public int punctualLightCount;
            public uint frameIndex;
            public bool taaEnabled;
            public IntPtr callback;
        }

        class NativeContactShadowsWaitPassData
        {
            public TextureHandle contactShadowsTexture;
            public IntPtr waitCallback;
        }

        TextureHandle RenderNativeContactShadows(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle depthTexture, BufferHandle lightList, int firstMipOffsetY, bool clearBuffer)
        {
            float contactShadowRange = Mathf.Clamp(m_ContactShadows.fadeDistance.value, 0.0f, m_ContactShadows.maxDistance.value);
            float contactShadowFadeEnd = m_ContactShadows.maxDistance.value;
            float contactShadowOneOverFadeRange = 1.0f / Math.Max(1e-6f, contactShadowRange);

            float contactShadowMinDist = Mathf.Min(m_ContactShadows.minDistance.value, contactShadowFadeEnd);
            float contactShadowFadeIn = Mathf.Clamp(m_ContactShadows.fadeInDistance.value, 1e-6f, contactShadowFadeEnd);

            Vector4 params1 = new Vector4(m_ContactShadows.length.value, m_ContactShadows.distanceScaleFactor.value, contactShadowFadeEnd, contactShadowOneOverFadeRange);
            Vector4 params2 = new Vector4(firstMipOffsetY, contactShadowMinDist, contactShadowFadeIn, m_ContactShadows.rayBias.value * 0.01f);
            Vector4 params3 = new Vector4(m_ContactShadows.sampleCount, m_ContactShadows.thicknessScale.value * 10.0f, 0.0f, 0.0f);
            int deferredShadowTileSize = 8; // Must match ContactShadows.compute / NativeContactShadows.comp.glsl.
            int numTilesX = (hdCamera.actualWidth + (deferredShadowTileSize - 1)) / deferredShadowTileSize;
            int numTilesY = (hdCamera.actualHeight + (deferredShadowTileSize - 1)) / deferredShadowTileSize;
            bool taaEnabled = hdCamera.frameSettings.IsEnabled(FrameSettingsField.Postprocess) &&
                hdCamera.antialiasing == HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;

            TextureHandle result;
            IntPtr waitCallback;
            using (var builder = renderGraph.AddUnsafePass<NativeContactShadowsPassData>("FSR3 Native Contact Shadows", out var passData))
            {
                passData.depthTexture = depthTexture;
                passData.contactShadowsTexture = renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
                { format = GraphicsFormat.R32_UInt, enableRandomWrite = true, clearBuffer = clearBuffer, clearColor = Color.clear, name = "ContactShadowsBuffer" });
                passData.lightList = lightList;
                passData.lightLoopLightData = m_LightLoopLightData;
                passData.params1 = params1;
                passData.params2 = params2;
                passData.params3 = params3;
                passData.screenSize = new Vector4(
                    hdCamera.actualWidth,
                    hdCamera.actualHeight,
                    1.0f / Mathf.Max(1, hdCamera.actualWidth),
                    1.0f / Mathf.Max(1, hdCamera.actualHeight));
                passData.invViewProj = hdCamera.mainViewConstants.invViewProjMatrix;
                passData.view = hdCamera.mainViewConstants.viewMatrix;
                passData.viewProj = hdCamera.mainViewConstants.viewProjMatrix;
                passData.proj = hdCamera.mainViewConstants.projMatrix;
                passData.actualWidth = hdCamera.actualWidth;
                passData.actualHeight = hdCamera.actualHeight;
                passData.numTilesX = numTilesX;
                passData.numTilesY = numTilesY;
                passData.viewCount = hdCamera.viewCount;
                passData.directionalLightCount = m_GpuLightsBuilder != null ? m_GpuLightsBuilder.directionalLightCount : 0;
                passData.punctualLightCount = m_GpuLightsBuilder != null ? m_GpuLightsBuilder.punctualLightCount : 0;
                passData.frameIndex = (uint)hdCamera.GetCameraFrameCount();
                passData.taaEnabled = taaEnabled;
                passData.callback = GetNativeContactShadowsDispatchCallback();
                waitCallback = s_NativeContactShadowsWaitCallback;

                builder.UseTexture(passData.depthTexture, AccessFlags.Read);
                builder.UseTexture(passData.contactShadowsTexture, AccessFlags.Write);
                builder.UseBuffer(passData.lightList, AccessFlags.Read);
                builder.AllowPassCulling(false);

                result = passData.contactShadowsTexture;

                builder.SetRenderFunc(
                    (NativeContactShadowsPassData data, UnsafeGraphContext ctx) =>
                    {
                        if (data.callback == IntPtr.Zero)
                            return;

                        Texture depth = data.depthTexture;
                        Texture output = data.contactShadowsTexture;
                        GraphicsBuffer lightListBuffer = data.lightList;
                        GraphicsBuffer lightData = data.lightLoopLightData.lightData;
                        GraphicsBuffer directionalLightData = data.lightLoopLightData.directionalLightData;
                        if (depth == null || output == null || lightListBuffer == null ||
                            lightData == null || directionalLightData == null)
                            return;

                        NativeContactShadowsParams nativeParams = default;
                        nativeParams.depth = depth.GetNativeTexturePtr();
                        nativeParams.output = output.GetNativeTexturePtr();
                        nativeParams.lightList = lightListBuffer.GetNativeBufferPtr();
                        nativeParams.lightData = lightData.GetNativeBufferPtr();
                        nativeParams.directionalLightData = directionalLightData.GetNativeBufferPtr();
                        nativeParams.width = (uint)Mathf.Max(1, data.actualWidth);
                        nativeParams.height = (uint)Mathf.Max(1, data.actualHeight);
                        nativeParams.viewCount = (uint)Mathf.Max(1, data.viewCount);
                        nativeParams.frameIndex = data.frameIndex;
                        nativeParams.numTilesX = (uint)Mathf.Max(1, data.numTilesX);
                        nativeParams.numTilesY = (uint)Mathf.Max(1, data.numTilesY);
                        nativeParams.directionalLightCount = (uint)Mathf.Max(0, data.directionalLightCount);
                        nativeParams.punctualLightCount = (uint)Mathf.Max(0, data.punctualLightCount);
                        nativeParams.taaEnabled = data.taaEnabled ? 1u : 0u;
                        nativeParams.params1 = data.params1;
                        nativeParams.params2 = data.params2;
                        nativeParams.params3 = data.params3;
                        nativeParams.screenSize = data.screenSize;
                        nativeParams.invViewProj = data.invViewProj;
                        nativeParams.view = data.view;
                        nativeParams.viewProj = data.viewProj;
                        nativeParams.proj = data.proj;

                        if (nativeParams.depth == IntPtr.Zero ||
                            nativeParams.output == IntPtr.Zero ||
                            nativeParams.lightList == IntPtr.Zero ||
                            nativeParams.lightData == IntPtr.Zero ||
                            nativeParams.directionalLightData == IntPtr.Zero)
                            return;

                        FSR3_NativeContactShadows_SetParams(nativeParams);
                        ctx.cmd.IssuePluginEvent(data.callback, kNativeContactShadowsEventId);

                        if ((s_NativeContactShadowsLogCounter++ % 120) == 0)
                        {
                            Debug.Log($"[FSR3DeckNativeContactShadows] native dispatch frame={nativeParams.frameIndex} res={nativeParams.width}x{nativeParams.height} tiles={nativeParams.numTilesX}x{nativeParams.numTilesY} dir={nativeParams.directionalLightCount} punctual={nativeParams.punctualLightCount} samples={(int)data.params3.x}");
                        }
                    });
            }

            // Phase 1d wait-migration (2026-05-16): producer-adjacent wait pass
            // collapses the parallel window between ContactShadows compute (on
            // slot 0) and any HDRP work that runs between this dispatch and
            // RenderDeferredLighting (the only consumer of contactShadowsBuffer).
            // Capture the wait state here; EmitDeferredNativeContactShadowsWait
            // (invoked from HDRenderPipeline.RenderGraph.cs immediately before
            // RenderDeferredLighting) emits the actual wait pass at the
            // consumer's render-graph time slot. Parallel window: ~6 render-
            // graph passes — ClearAndHeightFogVoxelization, RenderSSR,
            // RenderShadows (the big one), RenderScreenSpaceIndirectDiffuse,
            // RenderScreenSpaceShadows, GenerateMaxZPass.
            m_DeferredNativeContactShadowsWait = new NativeContactShadowsWaitState
            {
                valid = true,
                contactShadowsTexture = result,
                waitCallback = waitCallback,
            };

            return result;
        }

        // Phase 1d wait-migration: captured at producer-site time, consumed
        // by EmitDeferredNativeContactShadowsWait() at consumer site.
        struct NativeContactShadowsWaitState
        {
            public bool valid;
            public TextureHandle contactShadowsTexture;
            public IntPtr waitCallback;
        }

        NativeContactShadowsWaitState m_DeferredNativeContactShadowsWait;

        // Phase 1d wait-migration: consumer-site entry point. Invoke
        // immediately before the first graphics-queue consumer of the
        // contact-shadow mask (RenderDeferredLighting). No-op when native
        // contact-shadows didn't run (valid == false).
        void EmitDeferredNativeContactShadowsWait(RenderGraph renderGraph)
        {
            if (!m_DeferredNativeContactShadowsWait.valid)
                return;
            var s = m_DeferredNativeContactShadowsWait;
            m_DeferredNativeContactShadowsWait = default;

            using (var builder = renderGraph.AddUnsafePass<NativeContactShadowsWaitPassData>("FSR3 Native Contact Shadows Wait", out var passData))
            {
                passData.contactShadowsTexture = s.contactShadowsTexture;
                passData.waitCallback = s.waitCallback;
                builder.UseTexture(passData.contactShadowsTexture, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (NativeContactShadowsWaitPassData data, UnsafeGraphContext ctx) =>
                    {
                        if (data.waitCallback != IntPtr.Zero)
                            ctx.cmd.IssuePluginEvent(data.waitCallback, kNativeContactShadowsWaitEventId);
                    });
            }
        }

        TextureHandle RenderContactShadows(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle depthTexture, in BuildGPULightListOutput lightLists, int firstMipOffsetY)
        {
            if (!WillRenderContactShadow())
                return renderGraph.defaultResources.blackUIntTextureXR;

            bool nativeClearBuffer = m_CurrentDebugDisplaySettings.data.fullScreenDebugMode == FullScreenDebugMode.ContactShadows;
            bool nativeMsaa = hdCamera.msaaEnabled;
            bool nativeRayTracingEnabled = RayTracedContactShadowsRequired() && GetRayTracingState();
            bool nativeEligible = NativeContactShadowsReady() &&
                !nativeMsaa &&
                !nativeRayTracingEnabled &&
                hdCamera.viewCount == 1 &&
                lightLists.lightList.IsValid();

            if (nativeEligible)
            {
                TextureHandle nativeResult = RenderNativeContactShadows(renderGraph, hdCamera, depthTexture, lightLists.lightList, firstMipOffsetY, nativeClearBuffer);
                PushFullScreenDebugTexture(renderGraph, nativeResult, FullScreenDebugMode.ContactShadows);
                LogNativeAsyncCandidate("ContactShadows",
                    $"native=1 res={hdCamera.actualWidth}x{hdCamera.actualHeight} samples={m_ContactShadows.sampleCount} asyncRequested={hdCamera.frameSettings.ContactShadowsRunsAsync()} views={hdCamera.viewCount}");
                return nativeResult;
            }

            TextureHandle result;
            using (var builder = renderGraph.AddComputePass<RenderContactShadowPassData>("Contact Shadows", out var passData))
            {
                builder.EnableAsyncCompute(hdCamera.frameSettings.ContactShadowsRunsAsync());

                // Avoid garbage when visualizing contact shadows.
                bool clearBuffer = m_CurrentDebugDisplaySettings.data.fullScreenDebugMode == FullScreenDebugMode.ContactShadows;
                bool msaa = hdCamera.msaaEnabled;

                passData.contactShadowsCS = contactShadowComputeShader;
                passData.contactShadowsCS.shaderKeywords = null;
                if (msaa)
                {
                    passData.contactShadowsCS.EnableKeyword("ENABLE_MSAA");
                }

                passData.rayTracingEnabled = RayTracedContactShadowsRequired() && GetRayTracingState();
                if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing))
                {
                    passData.contactShadowsRTS = rayTracingResources.contactShadowRayTracingRT;
                    passData.accelerationStructure = RequestAccelerationStructure(hdCamera);

                    passData.actualWidth = hdCamera.actualWidth;
                    passData.actualHeight = hdCamera.actualHeight;
                }

                passData.kernel = s_deferredContactShadowKernel;

                float contactShadowRange = Mathf.Clamp(m_ContactShadows.fadeDistance.value, 0.0f, m_ContactShadows.maxDistance.value);
                float contactShadowFadeEnd = m_ContactShadows.maxDistance.value;
                float contactShadowOneOverFadeRange = 1.0f / Math.Max(1e-6f, contactShadowRange);

                float contactShadowMinDist = Mathf.Min(m_ContactShadows.minDistance.value, contactShadowFadeEnd);
                float contactShadowFadeIn = Mathf.Clamp(m_ContactShadows.fadeInDistance.value, 1e-6f, contactShadowFadeEnd);

                passData.params1 = new Vector4(m_ContactShadows.length.value, m_ContactShadows.distanceScaleFactor.value, contactShadowFadeEnd, contactShadowOneOverFadeRange);
                passData.params2 = new Vector4(firstMipOffsetY, contactShadowMinDist, contactShadowFadeIn, m_ContactShadows.rayBias.value * 0.01f);
                passData.params3 = new Vector4(m_ContactShadows.sampleCount, m_ContactShadows.thicknessScale.value * 10.0f, 0.0f, 0.0f);

                int deferredShadowTileSize = 8; // Must match ContactShadows.compute
                passData.numTilesX = (hdCamera.actualWidth + (deferredShadowTileSize - 1)) / deferredShadowTileSize;
                passData.numTilesY = (hdCamera.actualHeight + (deferredShadowTileSize - 1)) / deferredShadowTileSize;
                passData.viewCount = hdCamera.viewCount;

                LogNativeAsyncCandidate("ContactShadows",
                    $"res={hdCamera.actualWidth}x{hdCamera.actualHeight} tiles={passData.numTilesX}x{passData.numTilesY} samples={(int)passData.params3.x} asyncRequested={hdCamera.frameSettings.ContactShadowsRunsAsync()} rayTracing={passData.rayTracingEnabled} views={passData.viewCount}");

                passData.depthTextureParameterName = msaa ? HDShaderIDs._CameraDepthValuesTexture : HDShaderIDs._CameraDepthTexture;

                passData.lightLoopLightData = m_LightLoopLightData;
                passData.lightList = lightLists.lightList;
                builder.UseBuffer(passData.lightList, AccessFlags.Read);
                passData.depthTexture = depthTexture;
                builder.UseTexture(passData.depthTexture, AccessFlags.Read);
                passData.contactShadowsTexture = renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
                { format = GraphicsFormat.R32_UInt, enableRandomWrite = true, clearBuffer = clearBuffer, clearColor = Color.clear, name = "ContactShadowsBuffer" });
                builder.UseTexture(passData.contactShadowsTexture, AccessFlags.Write);

                result = passData.contactShadowsTexture;

                builder.SetRenderFunc(
                    (RenderContactShadowPassData data, ComputeGraphContext ctx) =>
                    {
                        ctx.cmd.SetComputeVectorParam(data.contactShadowsCS, HDShaderIDs._ContactShadowParamsParameters, data.params1);
                        ctx.cmd.SetComputeVectorParam(data.contactShadowsCS, HDShaderIDs._ContactShadowParamsParameters2, data.params2);
                        ctx.cmd.SetComputeVectorParam(data.contactShadowsCS, HDShaderIDs._ContactShadowParamsParameters3, data.params3);
                        ctx.cmd.SetComputeBufferParam(data.contactShadowsCS, data.kernel, HDShaderIDs._DirectionalLightDatas, data.lightLoopLightData.directionalLightData);

                        // Send light list to the compute
                        ctx.cmd.SetComputeBufferParam(data.contactShadowsCS, data.kernel, HDShaderIDs._LightDatas, data.lightLoopLightData.lightData);
                        ctx.cmd.SetComputeBufferParam(data.contactShadowsCS, data.kernel, HDShaderIDs.g_vLightListTile, data.lightList);

                        ctx.cmd.SetComputeTextureParam(data.contactShadowsCS, data.kernel, data.depthTextureParameterName, data.depthTexture);
                        ctx.cmd.SetComputeTextureParam(data.contactShadowsCS, data.kernel, HDShaderIDs._ContactShadowTextureUAV, data.contactShadowsTexture);

                        ctx.cmd.DispatchCompute(data.contactShadowsCS, data.kernel, data.numTilesX, data.numTilesY, data.viewCount);

                        if (data.rayTracingEnabled)
                        {
                            ctx.cmd.SetRayTracingShaderPass(data.contactShadowsRTS, "VisibilityDXR");
                            ctx.cmd.SetRayTracingAccelerationStructure(data.contactShadowsRTS, HDShaderIDs._RaytracingAccelerationStructureName, data.accelerationStructure);

                            ctx.cmd.SetRayTracingVectorParam(data.contactShadowsRTS, HDShaderIDs._ContactShadowParamsParameters, data.params1);
                            ctx.cmd.SetRayTracingVectorParam(data.contactShadowsRTS, HDShaderIDs._ContactShadowParamsParameters2, data.params2);
                            ctx.cmd.SetRayTracingBufferParam(data.contactShadowsRTS, HDShaderIDs._DirectionalLightDatas, data.lightLoopLightData.directionalLightData);

                            // Send light list to the compute
                            ctx.cmd.SetRayTracingBufferParam(data.contactShadowsRTS, HDShaderIDs._LightDatas, data.lightLoopLightData.lightData);
                            ctx.cmd.SetRayTracingBufferParam(data.contactShadowsRTS, HDShaderIDs.g_vLightListTile, data.lightList);

                            ctx.cmd.SetRayTracingTextureParam(data.contactShadowsRTS, HDShaderIDs._DepthTexture, data.depthTexture);
                            ctx.cmd.SetRayTracingTextureParam(data.contactShadowsRTS, HDShaderIDs._ContactShadowTextureUAV, data.contactShadowsTexture);

                            ctx.cmd.DispatchRays(data.contactShadowsRTS, "RayGenContactShadows", (uint)data.actualWidth, (uint)data.actualHeight, (uint)data.viewCount, null);
                        }
                    });
            }

            PushFullScreenDebugTexture(renderGraph, result, FullScreenDebugMode.ContactShadows);
            return result;
        }
    }
}
