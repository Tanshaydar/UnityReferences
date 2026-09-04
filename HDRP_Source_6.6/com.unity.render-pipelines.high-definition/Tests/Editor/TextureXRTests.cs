using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using System.Collections;
using UnityEngine.Rendering.HighDefinition;

// Important: TextureXR is only init and CleanedUP on HDRP as it rely on a compute shader for initiallization.
// If this change and TextureXR become also init on URP, we should move ths tests to Core package tests as they should have been from start (TextureXR is in Core pkg)
namespace UnityEngine.Rendering.Tests
{
    class TextureXRTests 
    {
        bool m_WasInitializedBeforeTests;
        Camera m_Camera;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (GraphicsSettings.currentRenderPipelineAssetType != typeof(HDRenderPipelineAsset))
                Assert.Ignore("This is an HDRP Tests, and the current pipeline is not HDRP.");

            m_WasInitializedBeforeTests = TextureXR.initialized;

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            var go = new GameObject("TestObject", typeof(Camera));
            m_Camera = go.GetComponent<Camera>();
        }

        [UnityOneTimeTearDown]
        public IEnumerator OneTimeTearDown()
        {
            if (m_WasInitializedBeforeTests)
                yield return new InitializeTextureXR(m_Camera);

            GameObject.DestroyImmediate(m_Camera.gameObject);
        }

        void AssertUninitialized()
        {
            Assert.That(TextureXR.initialized, Is.False, $"{nameof(TextureXR.initialized)} is not false after CleanUp");
            Assert.That(TextureXR.GetBlackUIntTexture(), Is.Null, $"{nameof(TextureXR.GetBlackUIntTexture)} is not null after CleanUp");
            Assert.That(TextureXR.GetClearTexture(), Is.Null, $"{nameof(TextureXR.GetClearTexture)} is not null after CleanUp");
            Assert.That(TextureXR.GetMagentaTexture(), Is.Null, $"{nameof(TextureXR.GetMagentaTexture)} is not null after CleanUp");
            Assert.That(TextureXR.GetBlackTexture(), Is.Null, $"{nameof(TextureXR.GetBlackTexture)} is not null after CleanUp");
            Assert.That(TextureXR.GetBlackTexture3D(), Is.Null, $"{nameof(TextureXR.GetBlackTexture3D)} is not null after CleanUp");
            Assert.That(TextureXR.GetWhiteTexture(), Is.Null, $"{nameof(TextureXR.GetWhiteTexture)} is not null after CleanUp");
        }

        void AssertValidRTHandle(RTHandle handle)
        {
            Assert.That(handle, Is.Not.Null);
            Assert.That(handle.nameID.ToString(), Does.Not.StartWith("Type None "), $"{handle.name} is not valid");
        }

        void AssertInitialized()
        {
            Assert.That(TextureXR.initialized, Is.True);
            AssertValidRTHandle(TextureXR.GetBlackUIntTexture());
            AssertValidRTHandle(TextureXR.GetClearTexture());
            AssertValidRTHandle(TextureXR.GetMagentaTexture());
            AssertValidRTHandle(TextureXR.GetBlackTexture());
            AssertValidRTHandle(TextureXR.GetBlackTexture3D());
            AssertValidRTHandle(TextureXR.GetWhiteTexture());
        }

        [UnityTest]
        public IEnumerator CheckInitialization()
        {
            TextureXR.Cleanup();
            AssertUninitialized();
            yield return new InitializeTextureXR(m_Camera);
            AssertInitialized();
            TextureXR.Cleanup();
            AssertUninitialized();
        }

        sealed class InitializeTextureXR : YieldInstruction
        {
            public InitializeTextureXR(Camera camera)
            {
                RenderTextureDescriptor desc = new RenderTextureDescriptor(1, 1, RenderTextureFormat.Default, 1); //short size to speed up. We don't really care what will be rendered
                var request = new RenderPipeline.StandardRequest();
                request.destination = RenderTexture.GetTemporary(desc);

                RenderPipeline.SubmitRenderRequest(camera, request); //The rendering of HDRP will call TextureXR.Initialize with RenderContext and right compute shader
                RenderTexture.ReleaseTemporary(request.destination);
            }
        }
    }
}
