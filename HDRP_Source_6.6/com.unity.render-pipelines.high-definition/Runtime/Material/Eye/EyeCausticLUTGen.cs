using Unity.Collections;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    internal class EyeCausticLUT
    {
        static class Uniforms
        {
            internal static readonly int _OutputWidth = Shader.PropertyToID("_OutputWidth");
            internal static readonly int _OutputHeight = Shader.PropertyToID("_OutputHeight");
            internal static readonly int _OutputDepth = Shader.PropertyToID("_OutputDepth");
            internal static readonly int _OutputSlice = Shader.PropertyToID("_OutputSlice");
            internal static readonly int _UseCorneaSymmetryMirroring = Shader.PropertyToID("_UseCorneaSymmetryMirroring");
            internal static readonly int _ExtraScleraMargin = Shader.PropertyToID("_ExtraScleraMargin");

            internal static readonly int _LightWidth = Shader.PropertyToID("_LightWidth");
            internal static readonly int _LightHeight = Shader.PropertyToID("_LightHeight");
            internal static readonly int _LightDistance = Shader.PropertyToID("_LightDistance");

            internal static readonly int _LightLuminousFlux = Shader.PropertyToID("_LightLuminousFlux");
            internal static readonly int _LightGrazingAngleCos = Shader.PropertyToID("_LightGrazingAngleCos");

            internal static readonly int _CorneaFlatteningFactor = Shader.PropertyToID("_CorneaFlatteningFactor");
            internal static readonly int _CorneaApproxPrimitive = Shader.PropertyToID("_CorneaApproxPrimitive");
            internal static readonly int _CorneaPowerFactor = Shader.PropertyToID("_CorneaPowerFactor");

            internal static readonly int _RandomNumbers = Shader.PropertyToID("_RandomNumbers");

            internal static readonly int _NumberOfSamplesAccumulated = Shader.PropertyToID("_NumberOfSamplesAccumulated");

            internal static readonly int _GeneratedSamplesBuffer = Shader.PropertyToID("_GeneratedSamplesBuffer");

            internal static readonly int _OutputLutTex = Shader.PropertyToID("_OutputLutTex");

            public static readonly int _PreIntegratedEyeCaustic = Shader.PropertyToID("_PreIntegratedEyeCaustic");
        }

        enum CorneaApproximationPrimitive
        {
            Sine = 0,
            Sphere = 1
        }

        private float lightSize = 10.0f;
        private float lightLuminousFlux = 12000;
        private float lightDipBelowHorizonDegrees = 30.0f;
        private float lightDistance = 10.0f;
        private float corneaFlatteningFactor = 0.7f;
        private float corneaPowerFactor = 1.5f;
        private CorneaApproximationPrimitive selectedCorneaApproxPrim = CorneaApproximationPrimitive.Sphere;

        private int lutWidth = 128;
        private int lutHeight = 32;
        private int lutDepth = 16;
        private bool useCorneaSymmetryMirroring = true;
        private float lutExtraScleraMargin = 0.15f;

        private RenderTexture generatedLUT;

        private ComputeBuffer generatedLutStagingBuffer;
        private ComputeBuffer randomSamplesBuffer;

        private const int LUT_GEN_KERNEL_SIZE = 64;
        private const int LUT_GEN_NUMBER_OF_DISPATCHES = 512 * 32;
        private const int LUT_GEN_SAMPLES_PER_SLICE = LUT_GEN_NUMBER_OF_DISPATCHES * LUT_GEN_KERNEL_SIZE;

        private ComputeShader m_Shader;
        private int m_SampleCausticKernel;
        private int m_CopyToLUTKernel;
        private int m_ClearBufferKernel;

        private void CreateLUTGenResources()
        {
            if (m_Shader == null)
            {
                m_Shader = GraphicsSettings.GetRenderPipelineSettings<HDRenderPipelineRuntimeShaders>().eyeMaterialCS;
                m_SampleCausticKernel = m_Shader.FindKernel("SampleCaustic");
                m_CopyToLUTKernel = m_Shader.FindKernel("CopyToLUT");
                m_ClearBufferKernel = m_Shader.FindKernel("ClearBuffer");
            }


            RenderTextureDescriptor volumeDesc = new RenderTextureDescriptor()
            {
                dimension = TextureDimension.Tex3D,
                width = lutWidth,
                height = lutHeight,
                volumeDepth = lutDepth,
                graphicsFormat = GraphicsFormat.R16_SFloat,
                enableRandomWrite = true,
                msaaSamples = 1,
            };

            if (generatedLUT != null)
            {
                generatedLUT.Release();
            }

            generatedLUT = new RenderTexture(volumeDesc)
            {
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave, name = "Caustic LUT"
            };
            generatedLUT.Create();

            generatedLutStagingBuffer = new ComputeBuffer(lutWidth * lutHeight, 4, ComputeBufferType.Raw);
            generatedLutStagingBuffer.name = "Caustic LUT Staging";
            randomSamplesBuffer = new ComputeBuffer(LUT_GEN_SAMPLES_PER_SLICE, sizeof(float) * 4, ComputeBufferType.Default);
        }

        private void FreeLUTGenResources()
        {
            randomSamplesBuffer.Release();
            generatedLutStagingBuffer.Release();

            m_Shader = null;
        }

        private void GenerateLUT()
        {
            for (int i = 0; i != lutDepth; ++i)
            {
                ClearStaging();
                GenerateLUTForSlice(i);
                CopyFromStagingToLUT(lutDepth - i - 1);
            }
        }

        private void GenerateLUTForSlice(int currentDepthSlice)
        {
            int sampleCount = LUT_GEN_SAMPLES_PER_SLICE;

            NativeArray<Vector4> samples = new NativeArray<Vector4>(sampleCount, Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);


            for (int i = 0; i < sampleCount; ++i)
            {
                float a = HaltonSequence.Get(i, 2);
                float b = HaltonSequence.Get(i, 3);
                float c = HaltonSequence.Get(i, 5);
                float d = HaltonSequence.Get(i, 7);

                Vector4 s = new Vector4(a, b, c, d);
                samples[i] = s;
            }

            randomSamplesBuffer.SetData(samples);
            samples.Dispose();

            CommandBuffer cmd = CommandBufferPool.Get();
            int kernel = m_SampleCausticKernel;

            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputWidth, lutWidth);
            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputHeight, lutHeight);
            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputSlice, currentDepthSlice);
            cmd.SetComputeFloatParam(m_Shader, Uniforms._ExtraScleraMargin, lutExtraScleraMargin);
            cmd.SetComputeFloatParam(m_Shader, Uniforms._UseCorneaSymmetryMirroring, useCorneaSymmetryMirroring ? 1 : 0);

            cmd.SetComputeFloatParam(m_Shader, Uniforms._LightWidth, lightSize);
            cmd.SetComputeFloatParam(m_Shader, Uniforms._LightHeight, lightSize);
            cmd.SetComputeFloatParam(m_Shader, Uniforms._LightDistance, lightDistance);
            float cosGrazingAngle = Mathf.Cos(Mathf.PI * 0.5f + Mathf.Deg2Rad * lightDipBelowHorizonDegrees);
            cmd.SetComputeFloatParam(m_Shader, Uniforms._LightGrazingAngleCos, cosGrazingAngle );
            cmd.SetComputeFloatParam(m_Shader, Uniforms._LightLuminousFlux, lightLuminousFlux );

            cmd.SetComputeIntParam(m_Shader, Uniforms._CorneaApproxPrimitive, (int)selectedCorneaApproxPrim);
            cmd.SetComputeFloatParam(m_Shader, Uniforms._CorneaFlatteningFactor, corneaFlatteningFactor);
            cmd.SetComputeFloatParam(m_Shader, Uniforms._CorneaPowerFactor, corneaPowerFactor);

            cmd.SetComputeBufferParam(m_Shader, kernel, Uniforms._RandomNumbers, randomSamplesBuffer);

            cmd.SetComputeBufferParam(m_Shader, kernel, Uniforms._GeneratedSamplesBuffer, generatedLutStagingBuffer);

            cmd.DispatchCompute(m_Shader, kernel, LUT_GEN_NUMBER_OF_DISPATCHES, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);

            CommandBufferPool.Release(cmd);
        }

        void CopyFromStagingToLUT(int currentDepthSlice)
        {

            int sampleCount = LUT_GEN_SAMPLES_PER_SLICE;
            CommandBuffer cmd = CommandBufferPool.Get();
            int kernel = m_CopyToLUTKernel;

            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputWidth, lutWidth);
            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputHeight, lutHeight);
            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputDepth, lutDepth);
            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputSlice, currentDepthSlice);
            cmd.SetComputeIntParam(m_Shader, Uniforms._NumberOfSamplesAccumulated, sampleCount);


            cmd.SetComputeBufferParam(m_Shader, kernel, Uniforms._GeneratedSamplesBuffer, generatedLutStagingBuffer);
            cmd.SetComputeTextureParam(m_Shader, kernel, Uniforms._OutputLutTex, generatedLUT);

            cmd.DispatchCompute(m_Shader, kernel, (lutWidth + 7) / 8,
                (lutHeight + 7) / 8, 1);

            Graphics.ExecuteCommandBuffer(cmd);

            CommandBufferPool.Release(cmd);
        }

        void ClearStaging()
        {
            CommandBuffer cmd = CommandBufferPool.Get();
            int kernel = m_ClearBufferKernel;

            int entries = lutWidth * lutHeight;

            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputWidth, lutWidth);
            cmd.SetComputeIntParam(m_Shader, Uniforms._OutputHeight, lutHeight);

            cmd.SetComputeBufferParam(m_Shader, kernel, Uniforms._GeneratedSamplesBuffer, generatedLutStagingBuffer);

            cmd.DispatchCompute(m_Shader, kernel, (entries + 63) / 64, 1, 1);

            Graphics.ExecuteCommandBuffer(cmd);

            CommandBufferPool.Release(cmd);
        }

        internal void Create()
        {
            CreateLUTGenResources();
            GenerateLUT();
            FreeLUTGenResources();
        }

        internal void Cleanup()
        {
            if (generatedLUT != null)
            {
                generatedLUT.Release();
                generatedLUT = null;
            }
        }

        internal void Bind(CommandBuffer cmd)
        {
            cmd.SetGlobalTexture(Uniforms._PreIntegratedEyeCaustic, generatedLUT);
        }
    }
}
