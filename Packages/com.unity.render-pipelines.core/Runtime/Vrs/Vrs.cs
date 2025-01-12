﻿using System;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Encapsulates variable shading rate support (VRS) and texture conversion to shading rate image
    /// </summary>
    public static class Vrs
    {
        class ConversionPassData
        {
            public TextureHandle sriTextureHandle;
            public TextureHandle mainTexHandle;
            public TextureDimension mainTexDimension;
            public BufferHandle mainTexLutHandle;
            public BufferHandle validatedShadingRateFragmentSizeHandle;
            public ComputeShader computeShader;
            public int kernelIndex;
            public Vector4 scaleBias;
            public Vector2Int dispatchSize;
            public bool yFlip;
        }

        class VisualizationPassData
        {
            public Material material;
            public TextureHandle source;
            public BufferHandle lut;
            public TextureHandle dummy;
            public Vector4 visualizationParams;
        }

        internal static readonly int shadingRateFragmentSizeCount = Enum.GetNames(typeof(ShadingRateFragmentSize)).Length;

        /// <summary>
        /// Checks if shading rate is supported.
        /// Convenience to abstract tier checks.
        /// </summary>
        /// <returns>True if shading rate is supported, false otherwise.</returns>
        public static bool IsSupported()
        {
            return (ShadingRateInfo.supportsPerDrawCall || ShadingRateInfo.supportsPerImageTile);
        }

        /// <summary>
        /// Checks if shading rate images are supported.
        /// Convenience to abstract tier checks
        /// </summary>
        /// <returns>True if shading rate images are supported, false otherwise.</returns>
        public static bool IsShadingRateImageSupported()
        {
            return ShadingRateInfo.supportsPerImageTile;
        }

        /// <summary>
        /// Check if conversion of color texture to shading rate image is supported.
        /// Convenience to abstract all capabilities checks.
        /// </summary>
        /// <returns>Returns true if conversion of color texture to shading rate image is supported, false otherwise.</returns>
        public static bool IsColorMaskTextureConversionSupported()
        {
            return SystemInfo.supportsComputeShaders &&
                   IsShadingRateImageSupported() &&
                   s_VrsResources?.textureComputeShader != null;
        }

        static VrsResources s_VrsResources;

        /// <summary>
        /// Preprocess resources found in VrsRenderPipelineRuntimeResources for use at runtime.
        /// </summary>
        public static void InitializeResources()
        {
            if (IsSupported())
            {
                var pipelineRuntimeResources = GraphicsSettings.GetRenderPipelineSettings<VrsRenderPipelineRuntimeResources>();
                s_VrsResources = new VrsResources(pipelineRuntimeResources);
            }
        }

        /// <summary>
        /// Cleanup resources.
        /// </summary>
        public static void DisposeResources()
        {
            s_VrsResources?.Dispose();
            s_VrsResources = null;
        }

        /// <summary>
        /// Converts a color mask texture to a shading rate image.
        /// </summary>
        /// <param name="renderGraph">Render graph to record conversion commands</param>
        /// <param name="sriRtHandle">Shading rate images to convert to.</param>
        /// <param name="colorMaskRtHandle">Texture to convert from.</param>
        /// <param name="yFlip">True if shading rate image should be generated flipped.</param>
        /// <returns>Shading rate image texture handle created.</returns>
        /// <remarks>
        /// sriRtHandle and colorMaskRtHandle are imported with renderGraph before doing the conversion.
        /// </remarks>
        public static TextureHandle ColorMaskTextureToShadingRateImage(RenderGraph renderGraph, RTHandle sriRtHandle, RTHandle colorMaskRtHandle, bool yFlip)
        {
            if (renderGraph == null || sriRtHandle == null || colorMaskRtHandle == null)
            {
                Debug.LogError($"TextureToShadingRateImage: invalid argument.");
                return TextureHandle.nullHandle;
            }

            var sriTextureHandle = renderGraph.ImportShadingRateImageTexture(sriRtHandle);
            var colorMaskHandle = renderGraph.ImportTexture(colorMaskRtHandle);

            return ColorMaskTextureToShadingRateImage(renderGraph,
                sriTextureHandle,
                colorMaskHandle,
                ((Texture)colorMaskRtHandle).dimension,
                yFlip);
        }

        /// <summary>
        /// Converts a color mask texture to a shading rate image.
        /// </summary>
        /// <param name="renderGraph">Render graph to record conversion commands</param>
        /// <param name="sriTextureHandle">Shading rate images to convert to.</param>
        /// <param name="colorMaskHandle">Texture to convert from.</param>
        /// <param name="colorMaskDimension">Texture's dimension.</param>
        /// <param name="yFlip">True if shading rate image should be generated flipped.</param>
        /// <returns>Shading rate image texture handle created.</returns>
        /// <remarks>
        /// sriRtHandle and colorMaskHandle are expected to be imported by renderGraph prior to this call.
        /// </remarks>
        public static TextureHandle ColorMaskTextureToShadingRateImage(RenderGraph renderGraph, TextureHandle sriTextureHandle, TextureHandle colorMaskHandle, TextureDimension colorMaskDimension, bool yFlip)
        {
            if (!IsColorMaskTextureConversionSupported())
            {
                Debug.LogError($"TextureToShadingRateImage: conversion not supported.");
                return TextureHandle.nullHandle;
            }

            var sriDesc = sriTextureHandle.GetDescriptor(renderGraph);
            if (sriDesc.dimension != TextureDimension.Tex2D)
            {
                Debug.LogError($"TextureToShadingRateImage: Vrs image not a texture 2D.");
                return TextureHandle.nullHandle;
            }

            if (colorMaskDimension != TextureDimension.Tex2D && colorMaskDimension != TextureDimension.Tex2DArray)
            {
                Debug.LogError($"TextureToShadingRateImage: Input texture dimension not supported.");
                return TextureHandle.nullHandle;
            }

            using (var builder = renderGraph.AddComputePass<ConversionPassData>("TextureToShadingRateImage", out var outerPassData, s_VrsResources.conversionProfilingSampler))
            {
                outerPassData.sriTextureHandle = sriTextureHandle;
                outerPassData.mainTexHandle = colorMaskHandle;
                outerPassData.mainTexDimension = colorMaskDimension;
                outerPassData.mainTexLutHandle = renderGraph.ImportBuffer(s_VrsResources.conversionLutBuffer);
                outerPassData.validatedShadingRateFragmentSizeHandle = renderGraph.ImportBuffer(s_VrsResources.validatedShadingRateFragmentSizeBuffer);

                outerPassData.computeShader = s_VrsResources.textureComputeShader;
                outerPassData.kernelIndex = s_VrsResources.textureReduceKernel;
                outerPassData.scaleBias = new Vector4()
                {
                    x = 1.0f / (sriDesc.width * s_VrsResources.tileSize.x),
                    y = 1.0f / (sriDesc.height * s_VrsResources.tileSize.y),
                    z = sriDesc.width,
                    w = sriDesc.height,
                };
                outerPassData.dispatchSize = new Vector2Int(sriDesc.width, sriDesc.height);
                outerPassData.yFlip = yFlip;

                builder.UseTexture(outerPassData.sriTextureHandle, AccessFlags.Write);
                builder.UseTexture(outerPassData.mainTexHandle);
                builder.UseBuffer(outerPassData.mainTexLutHandle);

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((ConversionPassData innerPassData, ComputeGraphContext context) =>
                {
                    ConversionDispatch(context.cmd, innerPassData);
                });

                return outerPassData.sriTextureHandle;
            }
        }

        /// <summary>
        /// Converts a shading rate image to a color texture for visualization.
        /// </summary>
        /// <param name="renderGraph">Render graph to record conversion commands</param>
        /// <param name="sriTextureHandle">Texture to convert from.</param>
        /// <param name="colorMaskHandle">Output of conversion.</param>
        public static void ShadingRateImageToColorMaskTexture(RenderGraph renderGraph, in TextureHandle sriTextureHandle, in TextureHandle colorMaskHandle)
        {
            using (var builder = renderGraph.AddRasterRenderPass<VisualizationPassData>("ShadingRateImageToTexture", out var outerPassData, s_VrsResources.visualizationProfilingSampler))
            {
                outerPassData.material = s_VrsResources.visualizationMaterial;

                if (sriTextureHandle.IsValid())
                    outerPassData.source = sriTextureHandle;
                else
                    outerPassData.source = renderGraph.defaultResources.blackTexture;

                outerPassData.lut = renderGraph.ImportBuffer(s_VrsResources.visualizationLutBuffer);
                outerPassData.dummy = renderGraph.defaultResources.blackTexture;
                outerPassData.visualizationParams = new Vector4(
                    1.0f / s_VrsResources.tileSize.x,
                    1.0f / s_VrsResources.tileSize.y,
                    0,
                    0);;

                builder.UseTexture(outerPassData.source);
                builder.UseBuffer(outerPassData.lut);
                builder.UseTexture(outerPassData.dummy);
                builder.SetRenderAttachment(colorMaskHandle, 0);

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((VisualizationPassData innerPassData, RasterGraphContext context) =>
                {
                    // must setup blitter source via the material: it's a typed texture (uint)
                    innerPassData.material.SetTexture(VrsShaders.s_ShadingRateImage, innerPassData.source);
                    innerPassData.material.SetBuffer(VrsShaders.s_VisualizationLut, innerPassData.lut);
                    innerPassData.material.SetVector(VrsShaders.s_VisualizationParams, innerPassData.visualizationParams);

                    Blitter.BlitTexture(context.cmd,
                        innerPassData.dummy,
                        new Vector4(1, 1, 0, 0),
                        innerPassData.material,
                        0);
                });
            }
        }

        static void ConversionDispatch(ComputeCommandBuffer cmd, ConversionPassData conversionPassData)
        {
            var disableTexture2dXArray = new LocalKeyword(conversionPassData.computeShader, VrsShaders.k_DisableTexture2dXArray);
            if (conversionPassData.mainTexDimension == TextureDimension.Tex2DArray)
                cmd.DisableKeyword(conversionPassData.computeShader, disableTexture2dXArray);
            else
                cmd.EnableKeyword(conversionPassData.computeShader, disableTexture2dXArray);

            var yFlip = new LocalKeyword(conversionPassData.computeShader, VrsShaders.k_YFlip);
            if (conversionPassData.yFlip)
                cmd.EnableKeyword(conversionPassData.computeShader, yFlip);
            else
                cmd.DisableKeyword(conversionPassData.computeShader, yFlip);

            cmd.SetComputeTextureParam(conversionPassData.computeShader, conversionPassData.kernelIndex, VrsShaders.s_MainTex, conversionPassData.mainTexHandle);
            cmd.SetComputeBufferParam(conversionPassData.computeShader, conversionPassData.kernelIndex, VrsShaders.s_MainTexLut, conversionPassData.mainTexLutHandle);
            cmd.SetComputeBufferParam(conversionPassData.computeShader, conversionPassData.kernelIndex, VrsShaders.s_ShadingRateNativeValues, conversionPassData.validatedShadingRateFragmentSizeHandle);
            cmd.SetComputeTextureParam(conversionPassData.computeShader, conversionPassData.kernelIndex, VrsShaders.s_ShadingRateImage, conversionPassData.sriTextureHandle);
            cmd.SetComputeVectorParam(conversionPassData.computeShader, VrsShaders.s_ScaleBias, conversionPassData.scaleBias);

            cmd.DispatchCompute(conversionPassData.computeShader, conversionPassData.kernelIndex, conversionPassData.dispatchSize.x, conversionPassData.dispatchSize.x, 1);
        }
    }
}
