using System.IO;
using System.Text;

namespace WavMarker
{
    /// <summary>
    /// Reads and writes standard RIFF WAV markers: the 'cue ' chunk plus 'labl' names
    /// inside a 'LIST'/'adtl' chunk. This is the format Audition, Reaper, Sound Forge etc. use.
    /// </summary>
    static class WavCues
    {
        public static List<Marker> Read(string path)
        {
            var result = new List<Marker>();
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 12) return result;
            string riff = ReadFourCC(br); br.ReadUInt32(); string wave = ReadFourCC(br);
            if ((riff != "RIFF" && riff != "RF64") || wave != "WAVE") return result;

            var cues = new List<(uint id, uint sampleOffset)>();
            var labels = new Dictionary<uint, string>();
            while (fs.Position + 8 <= fs.Length)
            {
                string id = ReadFourCC(br); uint size = br.ReadUInt32();
                long next = fs.Position + size + (size & 1);
                if (id == "cue ")
                {
                    uint n = br.ReadUInt32();
                    for (uint i = 0; i < n && fs.Position + 24 <= fs.Length; i++)
                    {
                        uint cueId = br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
                        uint off = br.ReadUInt32();
                        cues.Add((cueId, off));
                    }
                }
                else if (id == "LIST" && size >= 4)
                {
                    string type = ReadFourCC(br);
                    if (type == "adtl")
                    {
                        long end = fs.Position + size - 4;
                        while (fs.Position + 8 <= end)
                        {
                            string sid = ReadFourCC(br); uint ssize = br.ReadUInt32();
                            long snext = fs.Position + ssize + (ssize & 1);
                            if ((sid == "labl" || sid == "note") && ssize >= 4)
                            {
                                uint cueId = br.ReadUInt32();
                                var bytes = br.ReadBytes((int)ssize - 4);
                                int len = Array.IndexOf(bytes, (byte)0); if (len < 0) len = bytes.Length;
                                string text = Encoding.UTF8.GetString(bytes, 0, len);
                                if (sid == "labl" || !labels.ContainsKey(cueId)) labels[cueId] = text;
                            }
                            fs.Position = snext;
                        }
                    }
                }
                if (next > fs.Length) break;
                fs.Position = next;
            }
            foreach (var (id, off) in cues)
                result.Add(new Marker { Sample = off, Name = labels.TryGetValue(id, out var nm) ? nm : "" });
            result.Sort((a, b) => a.Sample.CompareTo(b.Sample));
            return result;
        }

        /// <summary>Rewrites the WAV with the given markers, preserving every other chunk.</summary>
        public static void Write(string path, IList<Marker> markers)
        {
            string tmp = path + ".spectromark.tmp";
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            using (var outFs = File.Create(tmp))
            using (var bw = new BinaryWriter(outFs))
            {
                string riff = ReadFourCC(br); br.ReadUInt32(); string wave = ReadFourCC(br);
                if (riff == "RF64") throw new NotSupportedException("RF64 (over 4 GB) WAV files are not supported for marker writing.");
                if (riff != "RIFF" || wave != "WAVE") throw new InvalidDataException("Not a RIFF WAVE file.");
                bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(0u); bw.Write(Encoding.ASCII.GetBytes("WAVE"));

                // copy every chunk except existing cue / adtl
                var buf = new byte[1 << 20];
                while (fs.Position + 8 <= fs.Length)
                {
                    string id = ReadFourCC(br); uint size = br.ReadUInt32();
                    long dataStart = fs.Position;
                    long total = size + (size & 1);
                    bool skip = id == "cue ";
                    if (id == "LIST" && size >= 4) { string type = ReadFourCC(br); fs.Position = dataStart; if (type == "adtl") skip = true; }
                    if (!skip)
                    {
                        bw.Write(Encoding.ASCII.GetBytes(id)); bw.Write(size);
                        long remaining = Math.Min(total, fs.Length - dataStart);
                        while (remaining > 0) { int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining)); if (n <= 0) break; outFs.Write(buf, 0, n); remaining -= n; }
                        if (remaining > 0) outFs.Write(new byte[remaining]); // truncated source: pad
                    }
                    fs.Position = dataStart + total;
                }

                // cue chunk
                bw.Write(Encoding.ASCII.GetBytes("cue ")); bw.Write((uint)(4 + 24 * markers.Count)); bw.Write((uint)markers.Count);
                for (int i = 0; i < markers.Count; i++)
                {
                    bw.Write((uint)(i + 1)); bw.Write((uint)markers[i].Sample); bw.Write(Encoding.ASCII.GetBytes("data"));
                    bw.Write(0u); bw.Write(0u); bw.Write((uint)markers[i].Sample);
                }
                // LIST adtl with labl per marker
                using var list = new MemoryStream();
                using var lw = new BinaryWriter(list);
                lw.Write(Encoding.ASCII.GetBytes("adtl"));
                for (int i = 0; i < markers.Count; i++)
                {
                    string name = string.IsNullOrEmpty(markers[i].Name) ? $"Marker {i + 1:00}" : markers[i].Name;
                    var text = Encoding.UTF8.GetBytes(name + "\0");
                    lw.Write(Encoding.ASCII.GetBytes("labl")); lw.Write((uint)(4 + text.Length)); lw.Write((uint)(i + 1)); lw.Write(text);
                    if ((text.Length & 1) == 1) lw.Write((byte)0);
                }
                bw.Write(Encoding.ASCII.GetBytes("LIST")); bw.Write((uint)list.Length); bw.Write(list.ToArray());

                outFs.Position = 4; bw.Write((uint)(outFs.Length - 8));
            }
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        static string ReadFourCC(BinaryReader br) => Encoding.ASCII.GetString(br.ReadBytes(4));
    }
}
