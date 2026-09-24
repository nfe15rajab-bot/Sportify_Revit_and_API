using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Sportify.Mechanical;

/// <summary>
/// Frames in, an H.264 MP4 out, with nothing but what Windows ships (Media Foundation's transcoder), so the pictures SOLIDWORKS renders become a video the web app can
/// play. The frames are pulled one at a time (BGRA bytes, width x height x 4), so a long film never sits in memory.
/// </summary>
internal static class Mp4Encoder
{
    /// <param name="frame">Returns the BGRA bytes of frame <c>i</c>, or null after the last one.</param>
    public static async Task EncodeAsync(string outPath, int width, int height, int fps, Func<int, byte[]?> frame, IProgress<int>? progress = null)
    {
        outPath = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        if (File.Exists(outPath)) File.Delete(outPath);

        var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        props.FrameRate.Numerator = (uint)fps; props.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(props)) { BufferTime = TimeSpan.Zero, CanSeek = false };
        var index = 0;
        var duration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
        source.SampleRequested += (_, args) =>
        {
            byte[]? bytes;
            try { bytes = frame(index); }
            catch (Exception) { bytes = null; }
            if (bytes == null || bytes.Length != width * height * 4) { args.Request.Sample = null; return; }
            // Media Foundation reads uncompressed RGB32 bottom-up; the callers' frames are top-down (row 0 is the top), so the rows go in reversed (found live: the first film came out upside down)
            var rowBytes = width * 4;
            var flipped = new byte[bytes.Length];
            for (var y = 0; y < height; y++) Buffer.BlockCopy(bytes, y * rowBytes, flipped, (height - 1 - y) * rowBytes, rowBytes);
            var sample = MediaStreamSample.CreateFromBuffer(flipped.AsBuffer(), TimeSpan.FromTicks(duration.Ticks * index));
            sample.Duration = duration;
            index++;
            progress?.Report(index);
            args.Request.Sample = sample;
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps; profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp(width * height * fps / 6, 1_500_000, 12_000_000);
        profile.Audio = null;

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outPath)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(outPath), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
        if (!prepared.CanTranscode) throw new InvalidOperationException("Windows cannot encode the video: " + prepared.FailureReason);
        await prepared.TranscodeAsync();
    }

    /// <summary>Reads a frame back out of a finished MP4 as a PNG (to see that the video decodes and shows what it should).</summary>
    public static async Task<string> ThumbnailAsync(string mp4, string pngPath, double atSeconds)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(mp4));
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using var thumb = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(atSeconds), 0, 0, VideoFramePrecision.NearestFrame);
        using var input = thumb.AsStreamForRead();
        using var output = File.Create(pngPath);
        await input.CopyToAsync(output);
        return pngPath + "  (" + clip.OriginalDuration.TotalSeconds.ToString("0.0") + " s, " + clip.GetVideoEncodingProperties().Width + "x" + clip.GetVideoEncodingProperties().Height + ")";
    }
}
