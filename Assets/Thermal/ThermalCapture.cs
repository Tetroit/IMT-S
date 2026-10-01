using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;


namespace IMT.Thermal
{
    /// <summary>
    /// Skeleton capture path: render to a single-channel float target, read it back, optionally
    /// write raw frames to disk. No physics here - this only proves the plumbing preserves values.
    ///
    /// Frames are written as little-endian float32, row-major, origin bottom-left, with a sidecar
    /// .json naming the dimensions. Raw rather than PNG or EXR because the consumer is numpy, and
    /// because any image encoder is another place values could be silently transformed.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("Thermal/Thermal Capture")]
    public class ThermalCapture : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] int m_Width = 640;
        [SerializeField] int m_Height = 512;

        [Header("Capture")]
        [SerializeField] bool m_WriteFrames;
        [SerializeField] string m_OutputDirectory = "Captures";

        [Header("Verification")]
        [Tooltip("Log the centre pixel each frame. Use this to confirm a known constant survives.")]
        [SerializeField] bool m_LogCentrePixel = true;

        Camera m_Camera;
        RenderTexture m_Target;
        float[] m_Pixels;
        bool m_ReadbackPending;
        int m_FrameIndex;

        /// <summary>The float target the camera renders into.</summary>
        public RenderTexture target { get { return m_Target; } }

        void OnEnable()
        {
            m_Camera = GetComponent<Camera>();

            m_Target = new RenderTexture(m_Width, m_Height, 24, RenderTextureFormat.RFloat,
                                         RenderTextureReadWrite.Linear)
            {
                name = "ThermalTarget",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            m_Target.Create();

            m_Camera.targetTexture = m_Target;

            // Post-processing would tonemap and colour-convert the values. There is no display
            // here; the buffer is a measurement.
            var data = m_Camera.GetUniversalAdditionalCameraData();
            if (data != null)
                data.renderPostProcessing = false;

            m_FrameIndex = 0;
        }

        void OnDisable()
        {
            if (m_Camera != null)
                m_Camera.targetTexture = null;

            if (m_Target != null)
            {
                m_Target.Release();
                if (Application.isPlaying) Destroy(m_Target); else DestroyImmediate(m_Target);
                m_Target = null;
            }
        }

        void LateUpdate()
        {
            if (m_Target == null || m_ReadbackPending || !SystemInfo.supportsAsyncGPUReadback)
                return;

            m_ReadbackPending = true;
            int index = m_FrameIndex++;

            AsyncGPUReadback.Request(m_Target, 0, TextureFormat.RFloat, request =>
            {
                m_ReadbackPending = false;
                if (request.hasError)
                {
                    Debug.LogError("[Thermal] readback failed");
                    return;
                }

                NativeArray<float> data = request.GetData<float>();
                if (m_Pixels == null || m_Pixels.Length != data.Length)
                    m_Pixels = new float[data.Length];
                data.CopyTo(m_Pixels);

                OnFrame(index);
            });
        }

        void OnFrame(int index)
        {
            if (m_LogCentrePixel)
            {
                int centre = (m_Height / 2) * m_Width + (m_Width / 2);
                Debug.Log(string.Format("[Thermal] frame {0}  centre = {1:R}", index, m_Pixels[centre]));
            }
            
            if (m_WriteFrames)
                WriteFrame(index);
        }

        void WriteFrame(int index)
        {
            string dir = Path.Combine(Application.dataPath, "..", m_OutputDirectory);
            Directory.CreateDirectory(dir);

            if (index == 0)
            {
                File.WriteAllText(Path.Combine(dir, "meta.json"),
                    string.Format("{{\"width\":{0},\"height\":{1},\"dtype\":\"float32\",\"origin\":\"bottom-left\"}}",
                                  m_Width, m_Height));
            }

            var bytes = new byte[m_Pixels.Length * sizeof(float)];
            System.Buffer.BlockCopy(m_Pixels, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dir, string.Format("frame_{0:D6}.raw", index)), bytes);
        }
    }
}
