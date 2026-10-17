using System;
using System.Runtime.InteropServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        bool m_AOHistoryReady = false;

        const string kFSR3DeckNativePluginName = "UnityFSR3DeckNative";
        const int kNativeSSAOEventId = 0x10011;
        const int kNativeSSAOWaitEventId = 0x10012;

        [StructLayout(LayoutKind.Sequential)]
        struct NativeSSAOParams
        {
            public IntPtr depth;
            public IntPtr normal;
            public IntPtr output;
            public uint width;
            public uint height;
            public uint fullResolution;
            public uint temporalAccumulation;
            public uint frameIndex;
            public uint pad0;
            public Vector4 aoBufferSize;
            public Vector4 aoParams0;
            public Vector4 aoParams1;
            public Vector4 aoParams2;
            public Vector4 aoParams3;
            public Vector4 aoParams4;
            public Vector4 firstTwoDepthMipOffsets;
            public Vector4 aoDepthToViewParams;
            public Vector4 zBufferParams;
            public Vector4 viewRow0;
            public Vector4 viewRow1;
            public Vector4 viewRow2;
        }

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern void FSR3_NativeSSAO_SetParams(NativeSSAOParams parameters);

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FSR3_NativeSSAO_GetDispatchCallback();

        [DllImport(kFSR3DeckNativePluginName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FSR3_NativeSSAO_GetWaitCallback();

        static int s_NativeSSAOEnvState = -1;
        static bool s_NativeSSAOProbeAttempted;
        static IntPtr s_NativeSSAOCallback = IntPtr.Zero;
        static IntPtr s_NativeSSAOWaitCallback = IntPtr.Zero;
        static bool s_NativeSSAOFallbackLogged;
        static int s_NativeSSAOLogCounter;

        float EvaluateSpecularOcclusionFlag(HDCamera hdCamera)
        {
            var ssoSettings = hdCamera.volumeStack.GetComponent<ScreenSpaceAmbientOcclusion>();
            bool enableRTAO = hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing) && ssoSettings.rayTracing.value;
            if (enableRTAO)
                return EvaluateRTSpecularOcclusionFlag(hdCamera, ssoSettings);
            else
                return 1.0f;
        }

        bool IsAmbientOcclusionActive(HDCamera camera, ScreenSpaceAmbientOcclusion settings) => camera.frameSettings.IsEnabled(FrameSettingsField.SSAO) && settings.intensity.value > 0f;

        struct RenderAOParameters
        {
            public Vector2 runningRes;
            public int viewCount;
            public bool fullResolution;
            public bool runAsync;
            public bool temporalAccumulation;
            public bool bilateralUpsample;
            public int targetWidth;
            public int targetHeight;
            public uint frameIndex;

            public ShaderVariablesAmbientOcclusion cb;
        }

        RenderAOParameters PrepareRenderAOParameters(HDCamera camera, Vector2 historySize, in HDUtils.PackedMipChainInfo depthMipInfo)
        {
            var parameters = new RenderAOParameters();

            ref var cb = ref parameters.cb;

            // Grab current settings
            var settings = camera.volumeStack.GetComponent<ScreenSpaceAmbientOcclusion>();
            parameters.fullResolution = settings.fullResolution;
            parameters.targetWidth = camera.actualWidth;
            parameters.targetHeight = camera.actualHeight;

            if (parameters.fullResolution)
            {
                parameters.runningRes = new Vector2(camera.actualWidth, camera.actualHeight);
                cb._AOBufferSize = new Vector4(camera.actualWidth, camera.actualHeight, 1.0f / camera.actualWidth, 1.0f / camera.actualHeight);
            }
            else
            {
                // Ceil is needed because we upsample the AO too, round would loose a pixel is the resolution is odd
                parameters.runningRes = new Vector2(Mathf.CeilToInt(camera.actualWidth * 0.5f), Mathf.CeilToInt(camera.actualHeight * 0.5f));
                cb._AOBufferSize = new Vector4(parameters.runningRes.x, parameters.runningRes.y, 1.0f / parameters.runningRes.x, 1.0f / parameters.runningRes.y);
            }

            parameters.temporalAccumulation = settings.temporalAccumulation.value && camera.frameSettings.IsEnabled(FrameSettingsField.MotionVectors);

            parameters.viewCount = camera.viewCount;
            parameters.runAsync = camera.frameSettings.SSAORunsAsync();
            float invHalfTanFOV = -camera.mainViewConstants.projMatrix[1, 1];
            float aspectRatio = parameters.runningRes.y / parameters.runningRes.x;
            uint frameCount = camera.GetCameraFrameCount();
            parameters.frameIndex = frameCount;

            cb._AOParams0 = new Vector4(
                parameters.fullResolution ? 0.0f : 1.0f,
                parameters.runningRes.y * invHalfTanFOV * 0.25f,
                settings.radius.value,
                settings.stepCount
            );

            cb._AOParams1 = new Vector4(
                settings.intensity.value,
                1.0f / (settings.radius.value * settings.radius.value),
                (frameCount / 6) % 4,
                (frameCount % 6)
            );


            // We start from screen space position, so we bake in this factor the 1 / resolution as well.
            cb._AODepthToViewParams = new Vector4(
                2.0f / (invHalfTanFOV * aspectRatio * parameters.runningRes.x),
                2.0f / (invHalfTanFOV * parameters.runningRes.y),
                1.0f / (invHalfTanFOV * aspectRatio),
                1.0f / invHalfTanFOV
            );

            float scaleFactor = (parameters.runningRes.x * parameters.runningRes.y) / (540.0f * 960.0f);
            float radInPixels = Mathf.Max(16, settings.maximumRadiusInPixels * Mathf.Sqrt(scaleFactor));

            cb._AOParams2 = new Vector4(
                historySize.x,
                historySize.y,
                1.0f / (settings.stepCount + 1.0f),
                radInPixels
            );

            float stepSize = settings.fullResolution ? 1 : 0.5f;

            float blurTolerance = 1.0f - settings.blurSharpness.value;
            float maxBlurTolerance = 0.25f;
            float minBlurTolerance = -2.5f;
            blurTolerance = minBlurTolerance + (blurTolerance * (maxBlurTolerance - minBlurTolerance));

            float bTolerance = 1f - Mathf.Pow(10f, blurTolerance) * stepSize;
            bTolerance *= bTolerance;
            const float upsampleTolerance = -7.0f; // TODO: Expose?
            float uTolerance = Mathf.Pow(10f, upsampleTolerance);
            float noiseFilterWeight = 1f / (Mathf.Pow(10f, 0.0f) + uTolerance);

            cb._AOParams3 = new Vector4(
                bTolerance,
                uTolerance,
                noiseFilterWeight,
                stepSize
            );

            float upperNudgeFactor = 1.0f - settings.ghostingReduction.value;
            const float maxUpperNudgeLimit = 5.0f;
            const float minUpperNudgeLimit = 0.25f;
            upperNudgeFactor = minUpperNudgeLimit + (upperNudgeFactor * (maxUpperNudgeLimit - minUpperNudgeLimit));
            cb._AOParams4 = new Vector4(
                settings.directionCount,
                upperNudgeFactor,
                minUpperNudgeLimit,
                settings.spatialBilateralAggressiveness.value * 15.0f
            );

            cb._FirstTwoDepthMipOffsets = new Vector4(depthMipInfo.mipLevelOffsets[1].x, depthMipInfo.mipLevelOffsets[1].y, depthMipInfo.mipLevelOffsets[2].x, depthMipInfo.mipLevelOffsets[2].y);

            parameters.bilateralUpsample = settings.bilateralUpsample;
            parameters.temporalAccumulation = settings.temporalAccumulation.value && camera.frameSettings.IsEnabled(FrameSettingsField.MotionVectors);

            parameters.viewCount = camera.viewCount;
            parameters.runAsync = camera.frameSettings.SSAORunsAsync();

            return parameters;
        }

        TextureHandle CreateAmbientOcclusionTexture(RenderGraph renderGraph, bool fullResolution)
        {
            if (fullResolution)
                return renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true) { enableRandomWrite = true, format = GraphicsFormat.R8_UNorm, name = "Ambient Occlusion" });
            else
                return renderGraph.CreateTexture(new TextureDesc(Vector2.one * 0.5f, true, true) { enableRandomWrite = true, format = GraphicsFormat.R32_SFloat, name = "Final Half Res AO Packed" });
        }

        static bool NativeSSAOEnabled()
        {
            if (!NativeSSAORuntimeSupported())
                return false;

            if (s_NativeSSAOEnvState < 0)
            {
                string value = Environment.GetEnvironmentVariable("TS_FSR3_NATIVE_SSAO")?.Trim();
                s_NativeSSAOEnvState =
                    string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                        ? 0 : 1;
            }

            return s_NativeSSAOEnvState == 1;
        }

        static bool NativeSSAORuntimeSupported()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan &&
                (Application.platform == RuntimePlatform.LinuxPlayer ||
                 Application.platform == RuntimePlatform.LinuxEditor);
        }

        static IntPtr GetNativeSSAODispatchCallback()
        {
            if (!NativeSSAOEnabled())
                return IntPtr.Zero;

            if (!s_NativeSSAOProbeAttempted)
            {
                s_NativeSSAOProbeAttempted = true;
                try
                {
                    s_NativeSSAOCallback = FSR3_NativeSSAO_GetDispatchCallback();
                    s_NativeSSAOWaitCallback = FSR3_NativeSSAO_GetWaitCallback();
                }
                catch (DllNotFoundException)
                {
                    s_NativeSSAOCallback = IntPtr.Zero;
                    s_NativeSSAOWaitCallback = IntPtr.Zero;
                }
                catch (EntryPointNotFoundException)
                {
                    s_NativeSSAOCallback = IntPtr.Zero;
                    s_NativeSSAOWaitCallback = IntPtr.Zero;
                }
            }

            return s_NativeSSAOCallback;
        }

        // Phase 1f-C diagnostic (2026-05-18): set true to disable native SSAO
        // for the FSR3-isolation A/B benchmark — only the FSR3 upscaler runs
        // on family-1 compute; everything else falls back to stock HDRP GTAO
        // on graphics queue. Used to test whether slot-0 FSR3 kernel
        // variability is caused by CU/memory contention with concurrent
        // SSAO/LightList native dispatches on family-1. Flip back to false
        // to restore native SSAO after the benchmark.
        const bool kNativeUpscalerOnlyDiagnostic_SSAO = false;

        static bool NativeSSAOReady()
        {
            if (kNativeUpscalerOnlyDiagnostic_SSAO) return false;
            return GetNativeSSAODispatchCallback() != IntPtr.Zero && s_NativeSSAOWaitCallback != IntPtr.Zero;
        }

        // Phase 1f-A early-dispatch (2026-05-18). Phase 1e telemetry measured
        // SSAO's parallelWindow at 0.05ms vs 1.1ms compute (ratio=0.05, hard
        // EXPOSED) because RenderAmbientOcclusion is called right before its
        // immediate consumer (SpatialDenoiseAO) — there's structurally no work
        // schedulable between dispatch and wait inside that pass. The fix:
        // submit the SSAO compute IssuePluginEvent EARLIER in the frame, right
        // after RenderPrepass (when SSAO inputs depth/normal/depthPyramid are
        // first ready), so the graphics-queue work between then and the wait
        // (BuildGPULightList, EvaluateHistoryValidationBuffer, then the rest
        // of RenderAmbientOcclusion's denoise chain) fills the parallel
        // window. The WAIT stays inside RenderAmbientOcclusion right before
        // SpatialDenoiseAO (where it must be — first consumer). The dispatch
        // output handle is stashed on the HDRenderPipeline instance so
        // RenderAmbientOcclusion can find it and skip the redundant dispatch
        // call.
        struct NativeSSAOEarlyDispatchState
        {
            public bool valid;
            public TextureHandle output;
            public RenderAOParameters parameters;
            public TextureHandle currentHistory;
            public TextureHandle outputHistory;
        }
        NativeSSAOEarlyDispatchState m_NativeSSAOEarlyDispatch;

        internal void EarlyDispatchNativeSSAOIfApplicable(RenderGraph renderGraph,
            HDCamera hdCamera, TextureHandle depthPyramid, TextureHandle normalBuffer,
            in HDUtils.PackedMipChainInfo depthMipInfo)
        {
            m_NativeSSAOEarlyDispatch = default;
            if (!NativeSSAOReady())
                return;

            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceAmbientOcclusion>();
            if (!IsAmbientOcclusionActive(hdCamera, settings))
                return;

            // RT-AO path doesn't route through native SSAO. Skip early dispatch
            // so RenderAmbientOcclusion's RT branch fires normally.
            if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing) &&
                settings.rayTracing.value && GetRayTracingState())
                return;

            m_AOHistoryReady = !hdCamera.AllocateAmbientOcclusionHistoryBuffer(settings.fullResolution ? 1.0f : 0.5f);
            var historyRT = hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.AmbientOcclusion);
            var currentHistory = renderGraph.ImportTexture(historyRT);
            var outputHistory = renderGraph.ImportTexture(hdCamera.GetPreviousFrameRT((int)HDCameraFrameHistoryType.AmbientOcclusion));

            Vector2 historySize = historyRT.GetScaledSize();
            var rtScaleForHistory = hdCamera.historyRTHandleProperties.rtHandleScale;
            var aoParameters = PrepareRenderAOParameters(hdCamera, historySize * rtScaleForHistory, depthMipInfo);

            var output = RenderNativeSSAO(renderGraph, hdCamera, aoParameters, depthPyramid, normalBuffer);

            m_NativeSSAOEarlyDispatch = new NativeSSAOEarlyDispatchState
            {
                valid = true,
                output = output,
                parameters = aoParameters,
                currentHistory = currentHistory,
                outputHistory = outputHistory,
            };
        }

        TextureHandle RenderAmbientOcclusion(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle depthBuffer, TextureHandle depthPyramid, TextureHandle normalBuffer, TextureHandle motionVectors, TextureHandle historyValidityBuffer,
            in HDUtils.PackedMipChainInfo depthMipInfo, ShaderVariablesRaytracing shaderVariablesRaytracing, TextureHandle rayCountTexture)
        {
            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceAmbientOcclusion>();

            TextureHandle result;
            if (IsAmbientOcclusionActive(hdCamera, settings))
            {
                using (new RenderGraphProfilingScope(renderGraph, ProfilingSampler.Get(HDProfileId.AmbientOcclusion)))
                {
                    if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing) && settings.rayTracing.value && GetRayTracingState())
                        result = RenderRTAO(renderGraph, hdCamera, depthBuffer, normalBuffer, motionVectors, historyValidityBuffer, rayCountTexture, shaderVariablesRaytracing);
                    else
                    {
                        // Phase 1f-A early-dispatch (2026-05-18): if EarlyDispatchNativeSSAOIfApplicable
                        // ran at the start of the frame (right after RenderPrepass), reuse its
                        // output handle + aoParameters + history textures instead of recomputing.
                        // The dispatch IssuePluginEvent already fired; this branch only emits the
                        // wait + denoise + upsample chain. The intervening BuildGPULightList +
                        // EvaluateHistoryValidationBuffer time is the parallel window we just
                        // opened for the SSAO compute work to overlap with.
                        TextureHandle currentHistory;
                        TextureHandle outputHistory;
                        RenderAOParameters aoParameters;
                        bool usePreDispatched = m_NativeSSAOEarlyDispatch.valid && NativeSSAOReady();
                        if (usePreDispatched)
                        {
                            aoParameters = m_NativeSSAOEarlyDispatch.parameters;
                            currentHistory = m_NativeSSAOEarlyDispatch.currentHistory;
                            outputHistory = m_NativeSSAOEarlyDispatch.outputHistory;
                        }
                        else
                        {
                            m_AOHistoryReady = !hdCamera.AllocateAmbientOcclusionHistoryBuffer(settings.fullResolution ? 1.0f : 0.5f);

                            var historyRT = hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.AmbientOcclusion);
                            currentHistory = renderGraph.ImportTexture(historyRT);
                            outputHistory = renderGraph.ImportTexture(hdCamera.GetPreviousFrameRT((int)HDCameraFrameHistoryType.AmbientOcclusion));

                            Vector2 historySize = historyRT.GetScaledSize();
                            var rtScaleForHistory = hdCamera.historyRTHandleProperties.rtHandleScale;

                            aoParameters = PrepareRenderAOParameters(hdCamera, historySize * rtScaleForHistory, depthMipInfo);
                        }

                        if (NativeSSAOReady())
                        {
                            // Phase 1a/1f-A wait-migration (2026-05-18). The wait pass is
                            // placed immediately before SpatialDenoiseAO — the first true
                            // consumer of raw SSAO output, which runs on the graphics
                            // queue and must read after the compute write completes. The
                            // dispatch itself was either (a) submitted EARLY by Earlier
                            // DispatchNativeSSAOIfApplicable right after RenderPrepass, in
                            // which case we reuse its output handle from the stashed state,
                            // or (b) submitted here inline as fallback when early dispatch
                            // didn't fire (e.g., RT-AO branch deciding factor changed
                            // between early and late, or some other gating condition).
                            // See HDRPPatches/manifest.json: ssao-early-dispatch for the
                            // package-level patch record.
                            if (usePreDispatched)
                            {
                                result = m_NativeSSAOEarlyDispatch.output;
                                // One-shot consume — clear the stash so next frame's state
                                // is fresh and any out-of-band call (e.g. dynamic resolution
                                // change forcing a re-dispatch) re-evaluates eligibility.
                                m_NativeSSAOEarlyDispatch = default;
                            }
                            else
                            {
                                result = RenderNativeSSAO(renderGraph, hdCamera, aoParameters, depthPyramid, normalBuffer);
                            }
                            WaitNativeSSAO(renderGraph, result, s_NativeSSAOWaitCallback);
                            if (aoParameters.temporalAccumulation || aoParameters.fullResolution)
                                result = SpatialDenoiseAO(renderGraph, aoParameters, result);
                            if (aoParameters.temporalAccumulation)
                                result = TemporalDenoiseAO(renderGraph, aoParameters, depthPyramid, motionVectors, result, currentHistory, outputHistory);
                            if (!aoParameters.fullResolution)
                                result = UpsampleAO(renderGraph, aoParameters, result, depthPyramid);
                        }
                        else
                        {
                            if (NativeSSAOEnabled() && !s_NativeSSAOFallbackLogged)
                            {
                                s_NativeSSAOFallbackLogged = true;
                                Debug.LogWarning("[FSR3DeckNativeSSAO] Native SSAO is expected, but the native dispatch/wait callbacks are unavailable; using HDRP GTAO.");
                            }

                            result = RenderAO(renderGraph, aoParameters, depthPyramid, normalBuffer);
                            if (aoParameters.temporalAccumulation || aoParameters.fullResolution)
                                result = SpatialDenoiseAO(renderGraph, aoParameters, result);
                            if (aoParameters.temporalAccumulation)
                                result = TemporalDenoiseAO(renderGraph, aoParameters, depthPyramid, motionVectors, result, currentHistory, outputHistory);
                            if (!aoParameters.fullResolution)
                                result = UpsampleAO(renderGraph, aoParameters, result, depthPyramid);
                        }
                    }
                }
            }
            else
            {
                result = renderGraph.defaultResources.blackTextureXR;
            }

            PushFullScreenDebugTexture(m_RenderGraph, result, FullScreenDebugMode.ScreenSpaceAmbientOcclusion);

            return result;
        }

        class NativeSSAOPassData
        {
            public RenderAOParameters parameters;
            public Vector4 zBufferParams;
            public Matrix4x4 viewMatrix;
            public UpscalerResources.ViewResourceHandles resourceHandles;
            public IntPtr callback;
            public IntPtr waitCallback;
        }

        TextureHandle RenderNativeSSAO(RenderGraph renderGraph, HDCamera hdCamera, in RenderAOParameters parameters, TextureHandle depthPyramid, TextureHandle normalBuffer)
        {
            TextureHandle output;
            IntPtr waitCallback;
            using (var builder = renderGraph.AddUnsafePass<NativeSSAOPassData>("FSR3 Native GTAO Horizon search", out var passData, ProfilingSampler.Get(HDProfileId.HorizonSSAO)))
            {
                passData.parameters = parameters;
                passData.zBufferParams = hdCamera.zBufferParams;
                passData.viewMatrix = hdCamera.mainViewConstants.viewMatrix;
                passData.callback = GetNativeSSAODispatchCallback();
                passData.waitCallback = s_NativeSSAOWaitCallback;
                float scaleFactor = parameters.fullResolution ? 1.0f : 0.5f;
                passData.resourceHandles = new UpscalerResources.ViewResourceHandles
                {
                    depth = depthPyramid,
                    normal = normalBuffer,
                    output = renderGraph.CreateTexture(new TextureDesc(Vector2.one * scaleFactor, true, true)
                    { format = GraphicsFormat.R32_SFloat, enableRandomWrite = true, name = "Native AO Packed data" }),
                };
                output = passData.resourceHandles.output;
                waitCallback = passData.waitCallback;

                builder.UseTexture(passData.resourceHandles.depth, AccessFlags.Read);
                builder.UseTexture(passData.resourceHandles.normal, AccessFlags.Read);
                builder.UseTexture(passData.resourceHandles.output, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (NativeSSAOPassData data, UnsafeGraphContext ctx) =>
                    {
                        if (data.callback == IntPtr.Zero)
                            return;

                        var resources = UpscalerResources.GetViewResources(data.resourceHandles);
                        if (resources.depth == null || resources.normal == null || resources.output == null)
                            return;

                        Matrix4x4 viewMatrix = data.viewMatrix;
                        NativeSSAOParams nativeParams = default;
                        nativeParams.depth = resources.depth.GetNativeTexturePtr();
                        nativeParams.normal = resources.normal.GetNativeTexturePtr();
                        nativeParams.output = resources.output.GetNativeTexturePtr();
                        nativeParams.width = (uint)Mathf.Max(1, (int)data.parameters.runningRes.x);
                        nativeParams.height = (uint)Mathf.Max(1, (int)data.parameters.runningRes.y);
                        nativeParams.fullResolution = data.parameters.fullResolution ? 1u : 0u;
                        nativeParams.temporalAccumulation = data.parameters.temporalAccumulation ? 1u : 0u;
                        nativeParams.frameIndex = data.parameters.frameIndex;
                        nativeParams.aoBufferSize = data.parameters.cb._AOBufferSize;
                        nativeParams.aoParams0 = data.parameters.cb._AOParams0;
                        nativeParams.aoParams1 = data.parameters.cb._AOParams1;
                        nativeParams.aoParams2 = data.parameters.cb._AOParams2;
                        nativeParams.aoParams3 = data.parameters.cb._AOParams3;
                        nativeParams.aoParams4 = data.parameters.cb._AOParams4;
                        nativeParams.firstTwoDepthMipOffsets = data.parameters.cb._FirstTwoDepthMipOffsets;
                        nativeParams.aoDepthToViewParams = data.parameters.cb._AODepthToViewParams;
                        nativeParams.zBufferParams = data.zBufferParams;
                        nativeParams.viewRow0 = new Vector4(viewMatrix.m00, viewMatrix.m01, viewMatrix.m02, 0.0f);
                        nativeParams.viewRow1 = new Vector4(viewMatrix.m10, viewMatrix.m11, viewMatrix.m12, 0.0f);
                        nativeParams.viewRow2 = new Vector4(viewMatrix.m20, viewMatrix.m21, viewMatrix.m22, 0.0f);

                        if (nativeParams.depth == IntPtr.Zero || nativeParams.normal == IntPtr.Zero || nativeParams.output == IntPtr.Zero)
                            return;

                        ConstantBuffer.UpdateData(ctx.cmd, data.parameters.cb);

                        FSR3_NativeSSAO_SetParams(nativeParams);
                        ctx.cmd.IssuePluginEvent(data.callback, kNativeSSAOEventId);

                        if ((s_NativeSSAOLogCounter++ % 120) == 0)
                        {
                            Debug.Log($"[FSR3DeckNativeSSAO] native GTAO dispatch frame={nativeParams.frameIndex} size={nativeParams.width}x{nativeParams.height} fullRes={nativeParams.fullResolution} temporal={nativeParams.temporalAccumulation} dirs={nativeParams.aoParams4.x:F0} steps={nativeParams.aoParams0.w:F0}");
                        }
                    });
            }
            // Phase 1a wait-migration (2026-05-16): producer no longer emits the
            // wait pass. Caller (RenderAmbientOcclusion) places WaitNativeSSAO at
            // the consumer's render-graph time slot via the public static
            // s_NativeSSAOWaitCallback. See HDRPPatches/manifest.json :
            // ssao-wait-migration.
            return output;
        }

        class NativeSSAOWaitPassData
        {
            public TextureHandle output;
            public IntPtr waitCallback;
        }

        void WaitNativeSSAO(RenderGraph renderGraph, TextureHandle output, IntPtr waitCallback)
        {
            using (var builder = renderGraph.AddUnsafePass<NativeSSAOWaitPassData>("FSR3 Native SSAO Wait", out var passData, ProfilingSampler.Get(HDProfileId.HorizonSSAO)))
            {
                passData.output = output;
                passData.waitCallback = waitCallback;

                builder.UseTexture(passData.output, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (NativeSSAOWaitPassData data, UnsafeGraphContext ctx) =>
                    {
                        if (data.waitCallback != IntPtr.Zero)
                            ctx.cmd.IssuePluginEvent(data.waitCallback, kNativeSSAOWaitEventId);
                    });
            }
        }

        class RenderAOPassData
        {
            public RenderAOParameters parameters;

            public ComputeShader gtaoCS;
            public int gtaoKernel;

            public TextureHandle packedData;
            public TextureHandle depthPyramid;
            public TextureHandle normalBuffer;
        }

        TextureHandle RenderAO(RenderGraph renderGraph, in RenderAOParameters parameters, TextureHandle depthPyramid, TextureHandle normalBuffer)
        {
            using (var builder = renderGraph.AddComputePass<RenderAOPassData>("GTAO Horizon search and integration", out var passData, ProfilingSampler.Get(HDProfileId.HorizonSSAO)))
            {
                builder.EnableAsyncCompute(parameters.runAsync);

                passData.parameters = parameters;
                passData.gtaoCS = runtimeShaders.GTAOCS;
                passData.gtaoCS.shaderKeywords = null;

                if (parameters.temporalAccumulation)
                    passData.gtaoCS.EnableKeyword("TEMPORAL");
                if (parameters.fullResolution)
                    passData.gtaoCS.EnableKeyword("FULL_RES");
                else
                    passData.gtaoCS.EnableKeyword("HALF_RES");

                passData.gtaoKernel = passData.gtaoCS.FindKernel("GTAOMain");

                float scaleFactor = parameters.fullResolution ? 1.0f : 0.5f;

                passData.packedData = renderGraph.CreateTexture(new TextureDesc(Vector2.one * scaleFactor, true, true)
                { format = GraphicsFormat.R32_SFloat, enableRandomWrite = true, name = "AO Packed data" });
                builder.UseTexture(passData.packedData, AccessFlags.Write);
                passData.depthPyramid = depthPyramid;
                builder.UseTexture(passData.depthPyramid, AccessFlags.Read);
                passData.normalBuffer = normalBuffer;
                builder.UseTexture(passData.normalBuffer, AccessFlags.Read);

                builder.SetRenderFunc(
                    (RenderAOPassData data, ComputeGraphContext ctx) =>
                    {
                        ConstantBuffer.Push(ctx.cmd, data.parameters.cb, data.gtaoCS, HDShaderIDs._ShaderVariablesAmbientOcclusion);
                        ctx.cmd.SetComputeTextureParam(data.gtaoCS, data.gtaoKernel, HDShaderIDs._AOPackedData, data.packedData);
                        ctx.cmd.SetComputeTextureParam(data.gtaoCS, data.gtaoKernel, HDShaderIDs._NormalBufferTexture, data.normalBuffer);
                        ctx.cmd.SetComputeTextureParam(data.gtaoCS, data.gtaoKernel, HDShaderIDs._CameraDepthTexture, data.depthPyramid);

                        const int groupSizeX = 8;
                        const int groupSizeY = 8;
                        int threadGroupX = ((int)data.parameters.runningRes.x + (groupSizeX - 1)) / groupSizeX;
                        int threadGroupY = ((int)data.parameters.runningRes.y + (groupSizeY - 1)) / groupSizeY;

                        ctx.cmd.DispatchCompute(data.gtaoCS, data.gtaoKernel, threadGroupX, threadGroupY, data.parameters.viewCount);
                    });

                return passData.packedData;
            }
        }

        class SpatialDenoiseAOPassData
        {
            public RenderAOParameters parameters;
            public ComputeShader spatialDenoiseAOCS;
            public int denoiseKernelSpatial;

            public TextureHandle packedData;
            public TextureHandle denoiseOutput;
        }

        TextureHandle SpatialDenoiseAO(RenderGraph renderGraph, in RenderAOParameters parameters, TextureHandle aoPackedData)
        {
            using (var builder = renderGraph.AddComputePass<SpatialDenoiseAOPassData>("Spatial Denoise GTAO", out var passData))
            {
                builder.EnableAsyncCompute(parameters.runAsync);

                float scaleFactor = parameters.fullResolution ? 1.0f : 0.5f;

                passData.parameters = parameters;

                passData.spatialDenoiseAOCS = runtimeShaders.GTAOSpatialDenoiseCS;
                passData.spatialDenoiseAOCS.shaderKeywords = null;
                if (parameters.temporalAccumulation)
                    passData.spatialDenoiseAOCS.EnableKeyword("TO_TEMPORAL");
                passData.denoiseKernelSpatial = passData.spatialDenoiseAOCS.FindKernel("SpatialDenoise");

                passData.packedData = aoPackedData;
                builder.UseTexture(passData.packedData, AccessFlags.Read);
                if (parameters.temporalAccumulation)
                {
                    passData.denoiseOutput = renderGraph.CreateTexture(
                        new TextureDesc(Vector2.one * (parameters.fullResolution ? 1.0f : 0.5f), true, true) { format = GraphicsFormat.R32_SFloat, enableRandomWrite = true, name = "AO Packed blurred data" });
                }
                else
                {
                    passData.denoiseOutput = CreateAmbientOcclusionTexture(renderGraph, parameters.fullResolution);
                }
                builder.UseTexture(passData.denoiseOutput, AccessFlags.Write);

                builder.SetRenderFunc(
                    (SpatialDenoiseAOPassData data, ComputeGraphContext ctx) =>
                    {
                        const int groupSizeX = 8;
                        const int groupSizeY = 8;
                        int threadGroupX = ((int)data.parameters.runningRes.x + (groupSizeX - 1)) / groupSizeX;
                        int threadGroupY = ((int)data.parameters.runningRes.y + (groupSizeY - 1)) / groupSizeY;

                        var blurCS = data.spatialDenoiseAOCS;
                        ConstantBuffer.Set<ShaderVariablesAmbientOcclusion>(ctx.cmd, blurCS, HDShaderIDs._ShaderVariablesAmbientOcclusion);

                        // Spatial
                        ctx.cmd.SetComputeTextureParam(blurCS, data.denoiseKernelSpatial, HDShaderIDs._AOPackedData, data.packedData);
                        ctx.cmd.SetComputeTextureParam(blurCS, data.denoiseKernelSpatial, HDShaderIDs._OcclusionTexture, data.denoiseOutput);
                        ctx.cmd.DispatchCompute(blurCS, data.denoiseKernelSpatial, threadGroupX, threadGroupY, data.parameters.viewCount);
                    });

                return passData.denoiseOutput;
            }
        }

        class TemporalDenoiseAOPassData
        {
            public RenderAOParameters parameters;

            public ComputeShader temporalDenoiseAOCS;
            public int denoiseKernelTemporal;
            public ComputeShader copyHistoryAOCS;
            public int denoiseKernelCopyHistory;
            public bool historyReady;

            public TextureHandle packedDataBlurred;
            public TextureHandle currentHistory;
            public TextureHandle outputHistory;
            public TextureHandle denoiseOutput;
            public TextureHandle motionVectors;
        }

        TextureHandle TemporalDenoiseAO(RenderGraph renderGraph,
            in RenderAOParameters parameters,
            TextureHandle depthTexture,
            TextureHandle motionVectors,
            TextureHandle aoPackedDataBlurred,
            TextureHandle currentHistory,
            TextureHandle outputHistory)
        {
            using (var builder = renderGraph.AddComputePass<TemporalDenoiseAOPassData>("Temporal Denoise GTAO", out var passData))
            {
                builder.EnableAsyncCompute(parameters.runAsync);

                float scaleFactor = parameters.fullResolution ? 1.0f : 0.5f;

                passData.parameters = parameters;
                passData.temporalDenoiseAOCS = runtimeShaders.GTAOTemporalDenoiseCS;
                passData.temporalDenoiseAOCS.shaderKeywords = null;
                if (parameters.fullResolution)
                    passData.temporalDenoiseAOCS.EnableKeyword("FULL_RES");
                else
                    passData.temporalDenoiseAOCS.EnableKeyword("HALF_RES");
                passData.denoiseKernelTemporal = passData.temporalDenoiseAOCS.FindKernel("TemporalDenoise");
                passData.copyHistoryAOCS = runtimeShaders.GTAOCopyHistoryCS;
                passData.denoiseKernelCopyHistory = passData.copyHistoryAOCS.FindKernel("GTAODenoise_CopyHistory");
                passData.historyReady = m_AOHistoryReady;

                passData.motionVectors = motionVectors;
                builder.UseTexture(passData.motionVectors, AccessFlags.Read);
                passData.currentHistory = currentHistory;
                builder.UseTexture(passData.currentHistory, AccessFlags.Read); // can also be written on first frame, but since it's an imported resource, it doesn't matter in term of lifetime.
                passData.outputHistory = outputHistory;
                builder.UseTexture(passData.outputHistory, AccessFlags.Write);
                passData.packedDataBlurred = aoPackedDataBlurred;
                builder.UseTexture(passData.packedDataBlurred, AccessFlags.Read);
                passData.denoiseOutput = CreateAmbientOcclusionTexture(renderGraph, parameters.fullResolution);
                builder.UseTexture(passData.denoiseOutput, AccessFlags.Write);

                builder.SetRenderFunc(
                    (TemporalDenoiseAOPassData data, ComputeGraphContext ctx) =>
                    {
                        const int groupSizeX = 8;
                        const int groupSizeY = 8;
                        int threadGroupX = ((int)data.parameters.runningRes.x + (groupSizeX - 1)) / groupSizeX;
                        int threadGroupY = ((int)data.parameters.runningRes.y + (groupSizeY - 1)) / groupSizeY;

                        if (!data.historyReady)
                        {
                            ctx.cmd.SetComputeTextureParam(data.copyHistoryAOCS, data.denoiseKernelCopyHistory, HDShaderIDs._InputTexture, data.packedDataBlurred);
                            ctx.cmd.SetComputeTextureParam(data.copyHistoryAOCS, data.denoiseKernelCopyHistory, HDShaderIDs._OutputTexture, data.currentHistory);
                            ctx.cmd.DispatchCompute(data.copyHistoryAOCS, data.denoiseKernelCopyHistory, threadGroupX, threadGroupY, data.parameters.viewCount);
                        }

                        var blurCS = data.temporalDenoiseAOCS;
                        ConstantBuffer.Set<ShaderVariablesAmbientOcclusion>(ctx.cmd, blurCS, HDShaderIDs._ShaderVariablesAmbientOcclusion);

                        // Temporal
                        ctx.cmd.SetComputeTextureParam(blurCS, data.denoiseKernelTemporal, HDShaderIDs._AOPackedBlurred, data.packedDataBlurred);
                        ctx.cmd.SetComputeTextureParam(blurCS, data.denoiseKernelTemporal, HDShaderIDs._AOPackedHistory, data.currentHistory);
                        ctx.cmd.SetComputeTextureParam(blurCS, data.denoiseKernelTemporal, HDShaderIDs._AOOutputHistory, data.outputHistory);
                        ctx.cmd.SetComputeTextureParam(blurCS, data.denoiseKernelTemporal, HDShaderIDs._CameraMotionVectorsTexture, data.motionVectors);
                        ctx.cmd.SetComputeTextureParam(blurCS, data.denoiseKernelTemporal, HDShaderIDs._OcclusionTexture, data.denoiseOutput);
                        ctx.cmd.DispatchCompute(blurCS, data.denoiseKernelTemporal, threadGroupX, threadGroupY, data.parameters.viewCount);
                    });

                return passData.denoiseOutput;
            }
        }

        class UpsampleAOPassData
        {
            public RenderAOParameters parameters;

            public ComputeShader upsampleAndBlurAOCS;
            public int upsampleAOKernel;

            public TextureHandle depthTexture;
            public TextureHandle input;
            public TextureHandle output;
        }

        TextureHandle UpsampleAO(RenderGraph renderGraph, in RenderAOParameters parameters, TextureHandle input, TextureHandle depthTexture)
        {
            if (parameters.fullResolution)
                return input;

            using (var builder = renderGraph.AddComputePass<UpsampleAOPassData>("Upsample GTAO", out var passData, ProfilingSampler.Get(HDProfileId.UpSampleSSAO)))
            {
                builder.EnableAsyncCompute(parameters.runAsync);

                passData.parameters = parameters;
                passData.upsampleAndBlurAOCS = runtimeShaders.GTAOBlurAndUpsample;
                if (parameters.temporalAccumulation)
                    passData.upsampleAOKernel = passData.upsampleAndBlurAOCS.FindKernel(parameters.bilateralUpsample ? "BilateralUpsampling" : "BoxUpsampling");
                else
                    passData.upsampleAOKernel = passData.upsampleAndBlurAOCS.FindKernel("BlurUpsample");
                passData.input = input;
                builder.UseTexture(passData.input, AccessFlags.Read);
                passData.depthTexture = depthTexture;
                builder.UseTexture(passData.depthTexture, AccessFlags.Read);
                passData.output = CreateAmbientOcclusionTexture(renderGraph, true);
                builder.UseTexture(passData.output, AccessFlags.Write);

                builder.SetRenderFunc(
                    (UpsampleAOPassData data, ComputeGraphContext ctx) =>
                    {
                        ConstantBuffer.Set<ShaderVariablesAmbientOcclusion>(ctx.cmd, data.upsampleAndBlurAOCS, HDShaderIDs._ShaderVariablesAmbientOcclusion);

                        ctx.cmd.SetComputeTextureParam(data.upsampleAndBlurAOCS, data.upsampleAOKernel, HDShaderIDs._AOPackedData, data.input);
                        ctx.cmd.SetComputeTextureParam(data.upsampleAndBlurAOCS, data.upsampleAOKernel, HDShaderIDs._OcclusionTexture, data.output);
                        ctx.cmd.SetComputeTextureParam(data.upsampleAndBlurAOCS, data.upsampleAOKernel, HDShaderIDs._CameraDepthTexture, data.depthTexture);

                        const int groupSizeX = 8;
                        const int groupSizeY = 8;
                        int threadGroupX = ((int)data.parameters.runningRes.x + (groupSizeX - 1)) / groupSizeX;
                        int threadGroupY = ((int)data.parameters.runningRes.y + (groupSizeY - 1)) / groupSizeY;
                        ctx.cmd.DispatchCompute(data.upsampleAndBlurAOCS, data.upsampleAOKernel, threadGroupX, threadGroupY, data.parameters.viewCount);
                    });

                return passData.output;
            }
        }

        void UpdateShaderVariableGlobalAmbientOcclusion(ref ShaderVariablesGlobal cb, HDCamera hdCamera)
        {
            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceAmbientOcclusion>();
            bool aoEnabled = false;
            if (IsAmbientOcclusionActive(hdCamera, settings))
            {
                aoEnabled = true;
                // If raytraced AO is enabled but raytracing state is wrong then we disable it.
                if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing) && settings.rayTracing.value && !HDRenderPipeline.currentPipeline.GetRayTracingState())
                {
                    aoEnabled = false;
                }
            }

            if (aoEnabled)
                cb._AmbientOcclusionParam = new Vector4(0f, 0f, 0f, settings.directLightingStrength.value);
            else
                cb._AmbientOcclusionParam = Vector4.zero;
        }
    }
}
