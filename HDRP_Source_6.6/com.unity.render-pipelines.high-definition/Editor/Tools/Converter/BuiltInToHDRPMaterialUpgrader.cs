using System;
using System.Collections.Generic;
using UnityEditor.Rendering.Converter;
using UnityEngine.Categorization;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    [Serializable]
    [PipelineConverter("Built-in", "High Definition Render Pipeline (HDRP)")]
    [ElementInfo(Name = "Material Shader Converter",
                 Order = 100,
                 Description = "This converter scans all materials that reference Built-in shaders and upgrades them to use High Definition Render Pipeline (HDRP) shaders.")]
    internal sealed class BuiltInToHDRPMaterialUpgrader : RenderPipelineConverterMaterialUpgrader
    {
        public override bool isEnabled
        {
            get
            {
                if (GraphicsSettings.currentRenderPipeline is not HDRenderPipelineAsset urpAsset)
                    return false;

                return true;
            }
        }

        public override string isDisabledMessage => "Converter requires HDRP. Convert your project to HDRP to use this converter.";

        protected override List<MaterialUpgrader> upgraders
        {
            get
            {
                var allHDRPUpgraders = MaterialUpgrader.FetchAllUpgradersForPipeline(typeof(HDRenderPipelineAsset));
                return allHDRPUpgraders;
            }
        }
            
    }
}
