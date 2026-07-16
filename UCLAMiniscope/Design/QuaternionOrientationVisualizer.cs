// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using Bonsai;
using Bonsai.Design;
using Bonsai.Vision.Design;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

[assembly: TypeVisualizer(typeof(UCLAMiniscope.Design.QuaternionOrientationVisualizer), Target = typeof(System.Numerics.Quaternion))]

namespace UCLAMiniscope.Design
{
    /// <summary>
    /// Displays a mouse-head model and direction arrow driven by a BNO055 quaternion
    /// (<see cref="System.Numerics.Quaternion"/> with components X,Y,Z,W).
    /// Uses the OpenGL context already provided by <see cref="VisualizerCanvas"/> —
    /// no extra packages required.
    /// </summary>
    public class QuaternionOrientationVisualizer : DialogTypeVisualizer
    {
        const float QuaternionNormTolerance = 0.05f;
        const float AmbientLight = 0.45f;

        volatile QuatSnapshot snapshot = new QuatSnapshot(0f, 0f, 0f, 1f);

        TableLayoutPanel layout;
        VisualizerCanvas canvas;
        Label cameraLabel;
        int program;
        int vaoArrow, vboArrow, arrowVertCount;
        int vaoHead,  vboHead,  headVertCount;
        int vaoEar,   vboEar,   earVertCount;
        int aPos, aNorm;
        int uModel, uView, uProj, uColor, uLight, uAmbient;
        float cameraYawDegrees;
        bool glReady;

        static readonly Vector3 DefaultCameraPosition = new Vector3(2.6f, -0.8f, 1.2f);
        static readonly Vector3 LightDirection = new Vector3(0.7f, -0.5f, 1.0f).Normalized();
        static readonly Matrix4 EarLeftLocal = Matrix4.CreateTranslation(-0.10f, 0.30f, 0.10f);
        static readonly Matrix4 EarRightLocal = Matrix4.CreateTranslation(-0.10f, -0.30f, 0.10f);
        static readonly Matrix4 ArrowLocal = Matrix4.CreateRotationZ(-MathHelper.PiOver2)
            * Matrix4.CreateTranslation(0.40f, 0f, 0f);

        // ── GLSL 1.20 shaders (OpenGL 2.0+, compatibility context) ─────────────

        const string VertSrc = @"#version 120
attribute vec3 aPos;
attribute vec3 aNorm;
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
varying vec3 fNorm;
void main() {
    gl_Position = uProj * uView * uModel * vec4(aPos, 1.0);
    fNorm = mat3(uModel) * aNorm;
}";
        // mat3(uModel) is valid for a pure-rotation matrix (no scale/translation):
        // inverse-transpose of an orthogonal matrix == the matrix itself.

        const string FragSrc = @"#version 120
varying vec3  fNorm;
uniform vec3  uColor;
uniform vec3  uLight;
uniform float uAmbient;
void main() {
    float d = max(dot(fNorm, uLight), 0.0);
    gl_FragColor = vec4((uAmbient + (1.0 - uAmbient) * d) * uColor, 1.0);
}";

        // ── Lifecycle ────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void Load(IServiceProvider provider)
        {
            layout = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                RowCount = 2,
                Size = new Size(240, 280)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));

            canvas = new VisualizerCanvas { Dock = DockStyle.Fill };
            canvas.Load        += (_, __) => InitGL();
            canvas.RenderFrame += (_, __) => Render();
            canvas.Canvas.Resize += (_, __) => UpdateProjection();

            var cameraControls = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                RowCount = 1
            };
            cameraControls.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            cameraControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            cameraLabel = new Label
            {
                Anchor = AnchorStyles.Left,
                AutoSize = true,
                Text = "Camera: 0°"
            };
            var cameraSlider = new TrackBar
            {
                Dock = DockStyle.Fill,
                LargeChange = 15,
                Maximum = 180,
                Minimum = -180,
                SmallChange = 5,
                TickFrequency = 45
            };
            cameraSlider.ValueChanged += (_, __) => UpdateCamera(cameraSlider.Value);

            cameraControls.Controls.Add(cameraLabel, 0, 0);
            cameraControls.Controls.Add(cameraSlider, 1, 0);
            layout.Controls.Add(canvas, 0, 0);
            layout.Controls.Add(cameraControls, 0, 1);

            var svc = (IDialogTypeVisualizerService)provider.GetService(typeof(IDialogTypeVisualizerService));
            svc?.AddControl(layout);
        }

        /// <inheritdoc/>
        public override void Show(object value)
        {
            var v = (System.Numerics.Quaternion)value;
            float normSquared = v.W * v.W + v.X * v.X + v.Y * v.Y + v.Z * v.Z;
            if (float.IsNaN(normSquared) || float.IsInfinity(normSquared)) return;

            float norm = (float)Math.Sqrt(normSquared);
            if (Math.Abs(norm - 1f) >= QuaternionNormTolerance) return;

            // Normalize accepted samples for rendering.
            float inverseNorm = 1f / norm;
            snapshot = new QuatSnapshot(
                v.X * inverseNorm,
                v.Y * inverseNorm,
                v.Z * inverseNorm,
                v.W * inverseNorm);
            canvas?.Canvas.Invalidate();
        }

        /// <inheritdoc/>
        public override void Unload()
        {
            if (canvas == null) return;
            if (glReady && !canvas.Canvas.IsDisposed)
            {
                canvas.MakeCurrent();
                GL.DeleteProgram(program);
                GL.DeleteVertexArray(vaoArrow); GL.DeleteBuffer(vboArrow);
                GL.DeleteVertexArray(vaoHead);  GL.DeleteBuffer(vboHead);
                GL.DeleteVertexArray(vaoEar);   GL.DeleteBuffer(vboEar);
            }
            glReady = false;
            layout.Dispose();
            layout = null;
            canvas = null;
            cameraLabel = null;
        }

        // ── GL initialisation (fires after VisualizerCanvas.canvas_Load) ────────

        void InitGL()
        {
            canvas.MakeCurrent();

            program  = CompileProgram(VertSrc, FragSrc);
            uModel   = GL.GetUniformLocation(program, "uModel");
            uView    = GL.GetUniformLocation(program, "uView");
            uProj    = GL.GetUniformLocation(program, "uProj");
            uColor   = GL.GetUniformLocation(program, "uColor");
            uLight   = GL.GetUniformLocation(program, "uLight");
            uAmbient = GL.GetUniformLocation(program, "uAmbient");
            aPos     = GL.GetAttribLocation(program, "aPos");
            aNorm    = GL.GetAttribLocation(program, "aNorm");

            GL.UseProgram(program);
            var view = CreateViewMatrix(cameraYawDegrees);
            GL.UniformMatrix4(uView, false, ref view);
            GL.Uniform3(uLight, LightDirection.X, LightDirection.Y, LightDirection.Z);
            GL.Uniform1(uAmbient, AmbientLight);

            // Arrow: 0.50 units long — placed at nose tip in Render
            UploadMesh(BuildArrow(16, 0.035f, 0.09f, 0.34f, 0.16f),  out vaoArrow, out vboArrow, out arrowVertCount);
            // The model uses the same reference frame as the configured IMU:
            // X forward, Y left, Z up.
            UploadMesh(BuildEllipsoid(12, 20, 0.40f, 0.28f, 0.16f),   out vaoHead,  out vboHead,  out headVertCount);
            // Ears: uniform sphere r=0.11, same mesh re-used for both sides
            UploadMesh(BuildEllipsoid(8,  12, 0.11f, 0.11f, 0.11f),   out vaoEar,   out vboEar,   out earVertCount);

            glReady = true;

            UpdateProjection();
        }

        void UpdateCamera(int yawDegrees)
        {
            cameraYawDegrees = yawDegrees;
            cameraLabel.Text = $"Camera: {yawDegrees}°";
            if (!glReady) return;

            canvas.MakeCurrent();
            GL.UseProgram(program);
            var view = CreateViewMatrix(cameraYawDegrees);
            GL.UniformMatrix4(uView, false, ref view);
            canvas.Canvas.Invalidate();
        }

        static Matrix4 CreateViewMatrix(float yawDegrees)
        {
            float yaw = MathHelper.DegreesToRadians(yawDegrees);
            float cos = (float)Math.Cos(yaw);
            float sin = (float)Math.Sin(yaw);
            var cameraPosition = new Vector3(
                DefaultCameraPosition.X * cos - DefaultCameraPosition.Y * sin,
                DefaultCameraPosition.X * sin + DefaultCameraPosition.Y * cos,
                DefaultCameraPosition.Z);

            return Matrix4.LookAt(cameraPosition, Vector3.Zero, Vector3.UnitZ);
        }

        void UpdateProjection()
        {
            if (!glReady) return;
            canvas.MakeCurrent();
            int w = canvas.Canvas.Width, h = canvas.Canvas.Height;
            if (w == 0 || h == 0) return;
            GL.Viewport(0, 0, w, h);
            GL.UseProgram(program);
            var proj = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45f), (float)w / h, 0.1f, 20f);
            GL.UniformMatrix4(uProj, false, ref proj);
        }

        void UploadMesh(float[] data, out int vao, out int vbo, out int vertCount)
        {
            vertCount = data.Length / 6;
            vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
            int stride  = 6 * sizeof(float);
            GL.VertexAttribPointer(aPos,  3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(aPos);
            GL.VertexAttribPointer(aNorm, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(aNorm);
            GL.BindVertexArray(0);
        }

        // ── Render ───────────────────────────────────────────────────────────────

        void Render()
        {
            if (!glReady) return;
            var snap = snapshot;

            // canvas_Paint already cleared ColorBuffer; clear depth separately.
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            GL.UseProgram(program);

            // The model and configured IMU use the same X-forward, Y-left, Z-up basis.
            var q = new Quaternion(snap.X, snap.Y, snap.Z, snap.W);
            var rot = Matrix4.CreateFromQuaternion(q);

            // ── Head body (pre-scaled ellipsoid, elongated along +X) ──────────────
            GL.Uniform3(uColor, 0.62f, 0.55f, 0.48f);   // warm beige
            GL.UniformMatrix4(uModel, false, ref rot);
            GL.BindVertexArray(vaoHead);
            GL.DrawArrays(PrimitiveType.Triangles, 0, headVertCount);

            // ── Ears (same sphere mesh, translated in the rotated frame) ──────────
            GL.Uniform3(uColor, 0.82f, 0.54f, 0.54f);   // pinkish
            GL.BindVertexArray(vaoEar);

            var earL = EarLeftLocal * rot;
            GL.UniformMatrix4(uModel, false, ref earL);
            GL.DrawArrays(PrimitiveType.Triangles, 0, earVertCount);

            var earR = EarRightLocal * rot;
            GL.UniformMatrix4(uModel, false, ref earR);
            GL.DrawArrays(PrimitiveType.Triangles, 0, earVertCount);

            // ── Arrow: points along +X from the nose tip (x = +0.40) ──────────────
            // Arrow mesh is along +Y; Rz(-90°) rotates +Y→+X; then translate to nose.
            GL.Uniform3(uColor, 0.10f, 0.82f, 1.00f);   // cyan
            var arrowModel = ArrowLocal * rot;
            GL.UniformMatrix4(uModel, false, ref arrowModel);
            GL.BindVertexArray(vaoArrow);
            GL.DrawArrays(PrimitiveType.Triangles, 0, arrowVertCount);

            GL.BindVertexArray(0);
        }

        // ── GL helpers ───────────────────────────────────────────────────────────

        static int CompileProgram(string vert, string frag)
        {
            int vs = CompileShader(ShaderType.VertexShader, vert, "VS");
            int fs = 0;
            int prog = 0;
            try
            {
                fs = CompileShader(ShaderType.FragmentShader, frag, "FS");
                prog = GL.CreateProgram();
                GL.AttachShader(prog, vs);
                GL.AttachShader(prog, fs);
                GL.LinkProgram(prog);
                GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int linkOk);
                if (linkOk == 0)
                    throw new Exception("Shader link: " + GL.GetProgramInfoLog(prog));

                return prog;
            }
            catch
            {
                if (prog != 0) GL.DeleteProgram(prog);
                throw;
            }
            finally
            {
                GL.DeleteShader(vs);
                if (fs != 0) GL.DeleteShader(fs);
            }
        }

        static int CompileShader(ShaderType type, string source, string label)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            if (ok != 0) return shader;

            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new Exception(label + " compile: " + log);
        }

        /// <summary>
        /// Builds a flat-shaded arrow pointing along +Y, base at origin, tip at y = shaftH+headH.
        /// Returns interleaved [px,py,pz, nx,ny,nz] floats (triangle list, no index buffer).
        /// </summary>
        static float[] BuildArrow(int segments, float shaftR, float headR, float shaftH, float headH)
        {
            var v = new List<float>(segments * 6 * 6);   // rough pre-alloc
            float mid = shaftH;
            float tip = shaftH + headH;

            void AddTri(float ax, float ay, float az,
                        float bx, float by, float bz,
                        float cx, float cy, float cz)
            {
                // Face normal via cross product (determines winding-consistent outward direction)
                float ex = bx - ax, ey = by - ay, ez = bz - az;
                float fx = cx - ax, fy = cy - ay, fz = cz - az;
                float nx = ey * fz - ez * fy;
                float ny = ez * fx - ex * fz;
                float nz = ex * fy - ey * fx;
                float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-7f) { nx /= len; ny /= len; nz /= len; }

                v.Add(ax); v.Add(ay); v.Add(az); v.Add(nx); v.Add(ny); v.Add(nz);
                v.Add(bx); v.Add(by); v.Add(bz); v.Add(nx); v.Add(ny); v.Add(nz);
                v.Add(cx); v.Add(cy); v.Add(cz); v.Add(nx); v.Add(ny); v.Add(nz);
            }

            for (int i = 0; i < segments; i++)
            {
                float a0 = (float)(2 * Math.PI * i       / segments);
                float a1 = (float)(2 * Math.PI * (i + 1) / segments);
                float c0 = (float)Math.Cos(a0), s0 = (float)Math.Sin(a0);
                float c1 = (float)Math.Cos(a1), s1 = (float)Math.Sin(a1);

                // Bottom cap  (normal ≈ -Y)
                AddTri(0, 0, 0,
                       c0 * shaftR, 0, s0 * shaftR,
                       c1 * shaftR, 0, s1 * shaftR);

                // Shaft side — two triangles per segment
                AddTri(c0 * shaftR, 0,   s0 * shaftR,
                       c1 * shaftR, 0,   s1 * shaftR,
                       c1 * shaftR, mid, s1 * shaftR);
                AddTri(c0 * shaftR, 0,   s0 * shaftR,
                       c1 * shaftR, mid, s1 * shaftR,
                       c0 * shaftR, mid, s0 * shaftR);

                // Head shoulder ring (annular face at y=mid, normal ≈ -Y)
                AddTri(c0 * shaftR, mid, s0 * shaftR,
                       c1 * headR,  mid, s1 * headR,
                       c0 * headR,  mid, s0 * headR);
                AddTri(c0 * shaftR, mid, s0 * shaftR,
                       c1 * shaftR, mid, s1 * shaftR,
                       c1 * headR,  mid, s1 * headR);

                // Cone side
                AddTri(c0 * headR, mid, s0 * headR,
                       c1 * headR, mid, s1 * headR,
                       0, tip, 0);
            }

            return v.ToArray();
        }

        /// <summary>
        /// Flat-shaded ellipsoid centred at origin with semi-axes (sx, sy, sz).
        /// Normals are computed per-face via cross product, so they are correct for
        /// any combination of rotation and translation in the model matrix.
        /// Interleaved [px,py,pz, nx,ny,nz] triangles.
        /// </summary>
        static float[] BuildEllipsoid(int rings, int sectors, float sx, float sy, float sz)
        {
            var v = new List<float>(rings * sectors * 2 * 3 * 6);

            void AddTri(float ax, float ay, float az,
                        float bx, float by, float bz,
                        float cx, float cy, float cz)
            {
                float ex = bx-ax, ey = by-ay, ez = bz-az;
                float fx = cx-ax, fy = cy-ay, fz = cz-az;
                float nx = ey*fz - ez*fy, ny = ez*fx - ex*fz, nz = ex*fy - ey*fx;
                float len = (float)Math.Sqrt(nx*nx + ny*ny + nz*nz);
                if (len > 1e-7f) { nx /= len; ny /= len; nz /= len; }
                v.Add(ax); v.Add(ay); v.Add(az); v.Add(nx); v.Add(ny); v.Add(nz);
                v.Add(bx); v.Add(by); v.Add(bz); v.Add(nx); v.Add(ny); v.Add(nz);
                v.Add(cx); v.Add(cy); v.Add(cz); v.Add(nx); v.Add(ny); v.Add(nz);
            }

            for (int r = 0; r < rings; r++)
            {
                double t0 = Math.PI * r       / rings;
                double t1 = Math.PI * (r + 1) / rings;
                for (int s = 0; s < sectors; s++)
                {
                    double p0 = 2 * Math.PI * s       / sectors;
                    double p1 = 2 * Math.PI * (s + 1) / sectors;

                    float x00 = sx*(float)(Math.Sin(t0)*Math.Cos(p0)), y00 = sy*(float)Math.Cos(t0), z00 = sz*(float)(Math.Sin(t0)*Math.Sin(p0));
                    float x01 = sx*(float)(Math.Sin(t0)*Math.Cos(p1)), y01 = sy*(float)Math.Cos(t0), z01 = sz*(float)(Math.Sin(t0)*Math.Sin(p1));
                    float x10 = sx*(float)(Math.Sin(t1)*Math.Cos(p0)), y10 = sy*(float)Math.Cos(t1), z10 = sz*(float)(Math.Sin(t1)*Math.Sin(p0));
                    float x11 = sx*(float)(Math.Sin(t1)*Math.Cos(p1)), y11 = sy*(float)Math.Cos(t1), z11 = sz*(float)(Math.Sin(t1)*Math.Sin(p1));

                    // At each pole, one of the usual quad triangles collapses
                    // to zero area. Omit it instead of uploading a zero normal.
                    if (r < rings - 1)
                        AddTri(x00,y00,z00, x10,y10,z10, x11,y11,z11);
                    if (r > 0)
                        AddTri(x00,y00,z00, x11,y11,z11, x01,y01,z01);
                }
            }
            return v.ToArray();
        }

        // ── Thread-safe quaternion snapshot ─────────────────────────────────────

        sealed class QuatSnapshot(float x, float y, float z, float w)
        {
            public readonly float X = x, Y = y, Z = z, W = w;
        }
    }
}
