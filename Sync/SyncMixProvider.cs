using NAudio.Wave;

namespace WavMarker.Sync
{
    /// <summary>Mixes all rendered tracks on the shared timeline into a stereo float stream.</summary>
    class SyncMixProvider : ISampleProvider
    {
        readonly List<SyncTrack> tracks;
        readonly long endFrame;
        public long Position;

        public SyncMixProvider(List<SyncTrack> tracks, int sampleRate, long startFrame, long endFrame)
        {
            this.tracks = tracks; this.endFrame = endFrame; Position = startFrame;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            long avail = endFrame - Position;
            if (avail <= 0) return 0;
            if (frames > avail) frames = (int)avail;
            Array.Clear(buffer, offset, frames * 2);
            bool anySolo = tracks.Any(t => t.Solo);
            foreach (var t in tracks)
            {
                if (t.Mute || (anySolo && !t.Solo)) continue;
                var r = t.Rendered; if (r == null) continue;
                long local0 = Position - t.Offset;
                int ch = r.Length;
                for (int i = 0; i < frames; i++)
                {
                    long li = local0 + i;
                    if (li < 0 || li >= t.RenderedLength) continue;
                    if (ch == 1) { float v = r[0][li]; buffer[offset + i * 2] += v; buffer[offset + i * 2 + 1] += v; }
                    else { buffer[offset + i * 2] += r[0][li]; buffer[offset + i * 2 + 1] += r[1][li]; }
                }
            }
            Position += frames;
            return frames * 2;
        }
    }
}
