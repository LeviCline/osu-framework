﻿// Copyright (c) 2007-2016 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu-framework/master/LICENCE

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using osu.Framework.DebugUtils;
using osu.Framework.Graphics.Batches;
using osu.Framework.Graphics.OpenGL.Textures;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Threading;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.ES20;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Textures;
using osu.Framework.Platform;
using osu.Framework.Statistics;

namespace osu.Framework.Graphics.OpenGL
{
    public static class GLWrapper
    {
        public static MaskingInfo CurrentMaskingInfo { get; private set; }
        public static Rectangle Viewport { get; private set; }
        public static Rectangle Ortho { get; private set; }
        public static Matrix4 ProjectionMatrix { get; private set; }

        public static bool UsingBackbuffer => lastFrameBuffer == 0;

        /// <summary>
        /// Check whether we have an initialised and non-disposed GL context.
        /// </summary>
        public static bool HasContext => GraphicsContext.CurrentContext != null;

        public static int MaxTextureSize { get; private set; }

        private static Scheduler resetScheduler = new Scheduler(null); //force no thread set until we are actually on the draw thread.

        public static bool IsInitialized { get; private set; }

        internal static void Initialize()
        {
            if (IsInitialized) return;

            resetScheduler.SetCurrentThread();

            MaxTextureSize = Math.Min(2048, GL.GetInteger(GetPName.MaxTextureSize));

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.StencilTest);
            GL.Enable(EnableCap.Blend);
            GL.Enable(EnableCap.ScissorTest);

            IsInitialized = true;
        }

        internal static void Reset(Vector2 size)
        {
            Debug.Assert(shaderStack.Count == 0);

            //todo: don't use scheduler
            resetScheduler.Update();

            lastBoundTexture = null;

            lastSrcBlend = BlendingFactorSrc.Zero;
            lastDestBlend = BlendingFactorDest.Zero;

            foreach (IVertexBatch b in thisFrameBatches)
                b.ResetCounters();
            thisFrameBatches.Clear();

            lastFrameBuffer = 0;

            viewportStack.Clear();
            orthoStack.Clear();
            maskingStack.Clear();

            Viewport = Rectangle.Empty;
            Ortho = Rectangle.Empty;

            PushViewport(new Rectangle(0, 0, (int)size.X, (int)size.Y));
            PushScissor(new MaskingInfo
            {
                ScreenSpaceAABB = new Rectangle(0, 0, (int)size.X, (int)size.Y),
                MaskingRect = new Primitives.RectangleF(0, 0, size.X, size.Y),
                ToMaskingSpace = Matrix3.Identity,
            }, true);
        }

        /// <summary>
        /// Enqueues a texture to be uploaded in the next frame.
        /// </summary>
        /// <param name="texture">The texture to be uploaded.</param>
        public static void EnqueueTextureUpload(TextureGL texture)
        {
            //todo: don't use scheduler
            resetScheduler.Add(() => texture.Upload());
        }

        private static int[] lastBoundBuffers = new int[2];

        /// <summary>
        /// Bind an OpenGL buffer object.
        /// </summary>
        /// <param name="target">The buffer type to bind.</param>
        /// <param name="buffer">The buffer ID to bind.</param>
        /// <returns>Whether an actual bind call was necessary. This value is false when repeatedly binding the same buffer.</returns>
        public static bool BindBuffer(BufferTarget target, int buffer)
        {
            int bufferIndex = target - BufferTarget.ArrayBuffer;
            if (lastBoundBuffers[bufferIndex] == buffer)
                return false;

            lastBoundBuffers[bufferIndex] = buffer;
            GL.BindBuffer(target, buffer);
            return true;
        }

        private static IVertexBatch lastActiveBatch;

        private static List<IVertexBatch> thisFrameBatches = new List<IVertexBatch>();

        /// <summary>
        /// Sets the last vertex batch used for drawing.
        /// <para>
        /// This is done so that various methods that change GL state can force-draw the batch
        /// before continuing with the state change.
        /// </para>
        /// </summary>
        /// <param name="batch">The batch.</param>
        internal static void SetActiveBatch(IVertexBatch batch)
        {
            if (lastActiveBatch == batch) return;

            FlushCurrentBatch();

            if (batch != null && !thisFrameBatches.Contains(batch))
                thisFrameBatches.Add(batch);

            lastActiveBatch = batch;
        }

        private static TextureGL lastBoundTexture = null;

        internal static bool AtlasTextureIsBound => lastBoundTexture is TextureGLAtlas;

        /// <summary>
        /// Binds a texture to darw with.
        /// </summary>
        /// <param name="texture"></param>
        public static void BindTexture(TextureGL texture)
        {
            if (lastBoundTexture != texture)
            {
                FlushCurrentBatch();

                GL.BindTexture(TextureTarget.Texture2D, texture?.TextureId ?? 0);
                lastBoundTexture = texture;

                FrameStatistics.Increment(StatisticsCounterType.TextureBinds);
            }
        }

        private static BlendingFactorSrc lastSrcBlend;
        private static BlendingFactorDest lastDestBlend;

        /// <summary>
        /// Sets the blending function to draw with.
        /// </summary>
        /// <param name="src">The source blending factor.</param>
        /// <param name="dest">The destination blending factor.</param>
        public static void SetBlend(BlendingFactorSrc src, BlendingFactorDest dest)
        {
            if (lastSrcBlend == src && lastDestBlend == dest)
                return;

            FlushCurrentBatch();

            GL.BlendFunc(src, dest);

            lastSrcBlend = src;
            lastDestBlend = dest;
        }

        private static int lastFrameBuffer = -1;

        /// <summary>
        /// Binds a framebuffer.
        /// </summary>
        /// <param name="frameBuffer">The framebuffer to bind.</param>
        /// <returns>The last bound framebuffer.</returns>
        public static int BindFrameBuffer(int frameBuffer)
        {
            if (lastFrameBuffer == frameBuffer)
                return lastFrameBuffer;

            FlushCurrentBatch();

            int last = lastFrameBuffer;

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, frameBuffer);
            lastFrameBuffer = frameBuffer;

            return last;
        }

        private static Stack<Rectangle> viewportStack = new Stack<Rectangle>();

        /// <summary>
        /// Applies a new viewport rectangle.
        /// </summary>
        /// <param name="viewport">The viewport rectangle.</param>
        public static void PushViewport(Rectangle viewport)
        {
            var actualRect = viewport;

            if (actualRect.Width < 0)
            {
                actualRect.X += viewport.Width;
                actualRect.Width = -viewport.Width;
            }

            if (actualRect.Height < 0)
            {
                actualRect.Y += viewport.Height;
                actualRect.Height = -viewport.Height;
            }

            PushOrtho(viewport);

            viewportStack.Push(Viewport);

            if (Viewport == actualRect)
                return;
            Viewport = actualRect;

            GL.Viewport(Viewport.Left, Viewport.Top, Viewport.Width, Viewport.Height);
        }

        /// <summary>
        /// Applies the last viewport rectangle.
        /// </summary>
        public static void PopViewport()
        {
            Debug.Assert(viewportStack.Count > 1);

            PopOrtho();

            viewportStack.Pop();
            Rectangle actualRect = viewportStack.Peek();

            if (Viewport == actualRect)
                return;
            Viewport = actualRect;

            GL.Viewport(Viewport.Left, Viewport.Top, Viewport.Width, Viewport.Height);
        }

        private static Stack<Rectangle> orthoStack = new Stack<Rectangle>();

        /// <summary>
        /// Applies a new orthographic projection rectangle.
        /// </summary>
        /// <param name="ortho">The orthographic projection rectangle.</param>
        public static void PushOrtho(Rectangle ortho)
        {
            orthoStack.Push(Ortho);

            FlushCurrentBatch();

            if (Ortho == ortho)
                return;
            Ortho = ortho;

            ProjectionMatrix = Matrix4.CreateOrthographicOffCenter(Ortho.Left, Ortho.Right, Ortho.Bottom, Ortho.Top, -1, 1);
            Shader.SetGlobalProperty(@"g_ProjMatrix", ProjectionMatrix);
        }

        /// <summary>
        /// Applies the last orthographic projection rectangle.
        /// </summary>
        public static void PopOrtho()
        {
            Debug.Assert(orthoStack.Count > 1);

            FlushCurrentBatch();

            orthoStack.Pop();
            Rectangle actualRect = orthoStack.Peek();

            if (Ortho == actualRect)
                return;
            Ortho = actualRect;

            ProjectionMatrix = Matrix4.CreateOrthographicOffCenter(Ortho.Left, Ortho.Right, Ortho.Bottom, Ortho.Top, -1, 1);
            Shader.SetGlobalProperty(@"g_ProjMatrix", ProjectionMatrix);
        }

        private static Stack<MaskingInfo> maskingStack = new Stack<MaskingInfo>();
        private static Rectangle currentScissorRect;

        private static void setMaskingQuad(MaskingInfo maskingInfo, bool overwritePreviousScissor)
        {
            FlushCurrentBatch();

            Shader.SetGlobalProperty(@"g_MaskingRect", new Vector4(
                maskingInfo.MaskingRect.Left,
                maskingInfo.MaskingRect.Top,
                maskingInfo.MaskingRect.Right,
                maskingInfo.MaskingRect.Bottom));

            Shader.SetGlobalProperty(@"g_ToMaskingSpace", maskingInfo.ToMaskingSpace);
            Shader.SetGlobalProperty(@"g_CornerRadius", maskingInfo.CornerRadius);

            Shader.SetGlobalProperty(@"g_BorderThickness", maskingInfo.BorderThickness);
            Shader.SetGlobalProperty(@"g_BorderColour", new Vector4(
                maskingInfo.BorderColour.R,
                maskingInfo.BorderColour.G,
                maskingInfo.BorderColour.B,
                maskingInfo.BorderColour.A));

            // We are setting the linear blend range to the approximate size of a _pixel_ here.
            // This results in the optimal trade-off between crispness and smoothness of the
            // edges of the masked region according to sampling theory.
            Vector3 scale = maskingInfo.ToMaskingSpace.ExtractScale();
            Shader.SetGlobalProperty(@"g_LinearBlendRange", (scale.X + scale.Y) / 2);

            Rectangle actualRect = maskingInfo.ScreenSpaceAABB;
            actualRect.X += Viewport.X;
            actualRect.Y += Viewport.Y;

            // Ensure the rectangle only has positive width and height. (Required by OGL)
            if (actualRect.Width < 0)
            {
                actualRect.X += actualRect.Width;
                actualRect.Width = -actualRect.Width;
            }

            if (actualRect.Height < 0)
            {
                actualRect.Y += actualRect.Height;
                actualRect.Height = -actualRect.Height;
            }

            if (overwritePreviousScissor)
                currentScissorRect = actualRect;
            else
                currentScissorRect.Intersect(actualRect);
            GL.Scissor(currentScissorRect.X, Viewport.Height - currentScissorRect.Bottom, currentScissorRect.Width, currentScissorRect.Height);
        }

        internal static void FlushCurrentBatch()
        {
            lastActiveBatch?.Draw();
        }

        /// <summary>
        /// Applies a new scissor rectangle.
        /// </summary>
        /// <param name="maskingInfo">The masking info.</param>
        public static void PushScissor(MaskingInfo maskingInfo, bool overwritePreviousScissor = false)
        {
            maskingStack.Push(maskingInfo);
            if (CurrentMaskingInfo.Equals(maskingInfo))
                return;

            CurrentMaskingInfo = maskingInfo;
            setMaskingQuad(CurrentMaskingInfo, overwritePreviousScissor);
        }

        /// <summary>
        /// Applies the last scissor rectangle.
        /// </summary>
        public static void PopScissor()
        {
            Debug.Assert(maskingStack.Count > 1);

            maskingStack.Pop();
            MaskingInfo maskingInfo = maskingStack.Peek();

            if (CurrentMaskingInfo.Equals(maskingInfo))
                return;

            CurrentMaskingInfo = maskingInfo;
            setMaskingQuad(CurrentMaskingInfo, true);
        }

        /// <summary>
        /// Deletes a framebuffer.
        /// </summary>
        /// <param name="frameBuffer">The framebuffer to delete.</param>
        internal static void DeleteFramebuffer(int frameBuffer)
        {
            if (frameBuffer == -1) return;

            //todo: don't use scheduler
            resetScheduler.Add(() => { GL.DeleteFramebuffer(frameBuffer); });
        }

        /// <summary>
        /// Deletes a buffer object.
        /// </summary>
        /// <param name="vboId">The buffer object to delete.</param>
        internal static void DeleteBuffer(int vboId)
        {
            //todo: don't use scheduler
            resetScheduler.Add(() => { GL.DeleteBuffer(vboId); });
        }

        /// <summary>
        /// Deletes textures.
        /// </summary>
        /// <param name="ids">An array of textures to delete.</param>
        internal static void DeleteTextures(params int[] ids)
        {
            //todo: don't use scheduler
            resetScheduler.Add(() => { GL.DeleteTextures(ids.Length, ids); });
        }

        /// <summary>
        /// Deletes a shader program.
        /// </summary>
        /// <param name="shader">The shader program to delete.</param>
        internal static void DeleteProgram(Shader shader)
        {
            //todo: don't use scheduler
            resetScheduler.Add(() => { GL.DeleteProgram(shader); });
        }

        /// <summary>
        /// Deletes a shader part.
        /// </summary>
        /// <param name="shaderPart">The shader part to delete.</param>
        internal static void DeleteShader(ShaderPart shaderPart)
        {
            //todo: don't use scheduler
            resetScheduler.Add(() => { GL.DeleteShader(shaderPart); });
        }

        private static int currentShader;

        private static Stack<int> shaderStack = new Stack<int>();

        public static void UseProgram(int? shader)
        {
            ThreadSafety.EnsureDrawThread();

            if (shader != null)
            {
                shaderStack.Push(shader.Value);
            }
            else
            {
                shaderStack.Pop();

                //check if the stack is empty, and if so don't restore the previous shader.
                if (shaderStack.Count == 0)
                    return;
            }

            int s = shader ?? shaderStack.Peek();

            if (currentShader == s) return;

            FlushCurrentBatch();

            GL.UseProgram(s);
            currentShader = s;
        }
    }

    public struct MaskingInfo : IEquatable<MaskingInfo>
    {
        public Rectangle ScreenSpaceAABB;
        public Primitives.RectangleF MaskingRect;

        /// <summary>
        /// This matrix transforms screen space coordinates to masking space (likely the parent
        /// space of the container doing the masking).
        /// It is used by a shader to determine which pixels to discard.
        /// </summary>
        public Matrix3 ToMaskingSpace;
        public float CornerRadius;

        public float BorderThickness;
        public Color4 BorderColour;

        public bool Equals(MaskingInfo other)
        {
            return
                ScreenSpaceAABB.Equals(other.ScreenSpaceAABB) &&
                MaskingRect.Equals(other.MaskingRect) &&
                ToMaskingSpace.Equals(other.ToMaskingSpace) &&
                CornerRadius == other.CornerRadius &&
                BorderThickness == other.BorderThickness &&
                BorderColour.Equals(other.BorderColour);
        }
    }
}
