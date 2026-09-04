using System.IO;
using NAudio.Wave;

namespace WavMarker
{
    public class Marker
    {
        public long Sample;
        public string Name = "";
    }

    public class AudioData
    {
        public float[][] Channels;
        public int SampleRate;
        public long Length;
        public int ChannelCount => Channels.Length;
        public double Duration => (double)Length / SampleRate;
    }

    static class AudioIO
    {
        public static AudioData Read(string path)
        {
            using var reader = new AudioFileReader(path);
            int ch = reader.WaveFormat.Channels;
            int sr = reader.WaveFormat.SampleRate;
            long totalFrames = reader.Length / (reader.WaveFormat.BitsPerSample / 8) / ch;
            var chans = new float[ch][];
            for (int c = 0; c < ch; c++) chans[c] = new float[totalFrames + 4096];
            var buf = new float[sr * ch];
            long pos = 0; int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                int frames = n / ch;
                if (pos + frames > chans[0].Length)
                    for (int c = 0; c < ch; c++) Array.Resize(ref chans[c], (int)(pos + frames + 65536));
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < ch; c++) chans[c][pos + i] = buf[i * ch + c];
                pos += frames;
            }
            for (int c = 0; c < ch; c++) Array.Resize(ref chans[c], (int)pos);
            return new AudioData { Channels = chans, SampleRate = sr, Length = pos };
        }

    }
}
