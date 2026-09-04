using NAudio.Wave;

namespace WavMarker.Sync
{
    /// <summary>Mixes all tracks on the shared timeline into a stereo float stream, reading straight through the segment maps.</summary>
    class SyncMixProvider : ISampleProvider
    {
        readonly List<SyncTrack> tracks;
        readonly long endFrame;
        public long Position;
        public Func<SyncTrack> TempSolo;   // lane soloed while a key is held, or null
        float[] tmp = new float[0];

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
            var temp = TempSolo?.Invoke();
            bool anySolo = tracks.Any(t => t.Solo);
            foreach (var t in tracks)
            {
                if (temp != null ? t != temp : (t.Mute || (anySolo && !t.Solo))) continue;
                long local0 = Position - t.Offset;
                if (local0 + frames <= 0 || local0 >= t.RenderedLength) continue;
                if (t.Audio.ChannelCount == 1)
                {
                    if (tmp.Length < frames) tmp = new float[frames];
                    Array.Clear(tmp, 0, frames);
                    t.AddInto(tmp, 0, 0, local0, frames);
                    for (int i = 0; i < frames; i++) { buffer[offset + i * 2] += tmp[i]; buffer[offset + i * 2 + 1] += tmp[i]; }
                }
                else
                {
                    t.AddInto(buffer, offset, 0, local0, frames, 2);
                    t.AddInto(buffer, offset + 1, 1, local0, frames, 2);
                }
            }
            Position += frames;
            return frames * 2;
        }
    }
}
