#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor.Media;
using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>
    /// Writes an H.264 MP4 using Unity's own MediaEncoder (an editor module:
    /// no package, no ffmpeg, nothing to install). The simulation runs inside
    /// the Editor in play mode - that is how Revit launches it - so an
    /// editor-only encoder is available everywhere it is needed.
    ///
    /// Frames are rendered by hand, one per call, from the camera into a render
    /// texture and read straight back. That makes the video independent of how
    /// fast the machine renders: the recording is a function of the simulation,
    /// not of the frame rate it happened to run at.
    /// </summary>
    public sealed class VideoRecorder : IDisposable
    {
        readonly Camera _camera;
        readonly RenderTexture _target;
        readonly Texture2D _frame;
        MediaEncoder _encoder;

        public string Path { get; }
        public int Width { get; }
        public int Height { get; }
        public int Fps { get; }
        public int FramesWritten { get; private set; }

        public VideoRecorder(Camera camera, string path, int width, int height, int fps)
        {
            _camera = camera;
            Path = path;
            Width = width;
            Height = height;
            Fps = fps;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Delete(path);

            var h264 = new H264EncoderAttributes
            {
                gopSize = (uint)(fps * 2),
                numConsecutiveBFrames = 2,
                profile = PickEnum<UnityEditor.VideoEncodingProfile>("High", "Main"),
            };
            var attributes = new VideoTrackEncoderAttributes(h264)
            {
                frameRate = new MediaRational(fps),
                width = (uint)width,
                height = (uint)height,
                includeAlpha = false,
                bitRateMode = PickEnum<UnityEditor.VideoBitrateMode>("High"),
                targetBitRate = (uint)(width * height * fps / 6),   // ~10 Mbit/s at 1080p30
            };

            _encoder = new MediaEncoder(path, attributes);

            _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            _target.Create();
            _frame = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        /// <summary>Renders the camera once and appends that frame to the video.</summary>
        public void CaptureFrame()
        {
            var previousTarget = _camera.targetTexture;
            var previousActive = RenderTexture.active;

            _camera.targetTexture = _target;
            _camera.Render();
            _camera.targetTexture = previousTarget;

            RenderTexture.active = _target;
            _frame.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            RenderTexture.active = previousActive;

            _encoder.AddFrame(_frame);
            FramesWritten++;
        }

        /// <summary>Saves the most recently captured frame as a PNG (poster image / debugging).</summary>
        public void SaveStill(string pngPath)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pngPath));
            File.WriteAllBytes(pngPath, _frame.EncodeToPNG());
        }

        /// <summary>Finalises the MP4 (writes its index). Must run before the process exits.</summary>
        public void Dispose()
        {
            if (_encoder != null)
            {
                _encoder.Dispose();
                _encoder = null;
            }

            if (_target != null) _target.Release();
            if (_target != null) UnityEngine.Object.Destroy(_target);
            if (_frame != null) UnityEngine.Object.Destroy(_frame);
        }

        // The encoder's enums differ between editor versions; choose by name, falling back to the last value.
        static T PickEnum<T>(params string[] preferred) where T : struct
        {
            var names = Enum.GetNames(typeof(T));
            foreach (var wanted in preferred)
            {
                foreach (var name in names)
                {
                    if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                        return (T)Enum.Parse(typeof(T), name);
                }
            }

            var values = Enum.GetValues(typeof(T));
            return (T)values.GetValue(values.Length - 1);
        }
    }
}
#endif
