// 녹화 결과 내보내기 (2026-10-24, StudioManager.Record). 메인 스레드는 큐에 넣기만 하고 쓰기는 전용 스레드에서 한다.
//   RecordPipe  MP4용: Host(Core.RecordJob)가 연 이름 있는 파이프로 프레임을 바로 보낸다 (예전엔 프레임마다 raw 파일 — 4K면 33 MB를 디스크에 쓰고 읽고 지웠다).
//               형식(리틀 엔디언): 머리말 "VPR1" · 너비 · 높이 · fps · 플래그(1 = 소리 있음)
//                                 레코드 1 = 프레임: 같은 그림 장 수 · 너비×높이×4 바이트(위에서 아래로 BGRA)  (실시간 녹화에서 늦은 만큼 같은 장을 되풀이)
//                                 레코드 2 = 소리: 샘플레이트 · 샘플 수 · 16비트 모노 PCM
//                                 레코드 3 = 끝 (없이 끊기면 Host는 취소로 본다)
//   PngWriter   PNG 연속 이미지: PNG 인코딩·파일 쓰기를 스레드에서 (같은 그림을 되풀이하면 파일 복사).
//   Wav         16 kHz 말하기 녹음 → 48 kHz 16비트 (MP4 AAC 인코더는 44.1·48 kHz만 받는다), WAV 파일 쓰기.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace VrcProto.Viewer
{
    public sealed class RecordPipe
    {
        public const int TypeFrame = 1, TypeAudio = 2, TypeEnd = 3;
        readonly NamedPipeClientStream _pipe;
        readonly BinaryWriter _w;
        readonly BlockingCollection<(int Type, int Count, byte[] Data, short[] Pcm, int Rate)> _q = new BlockingCollection<(int, int, byte[], short[], int)>();
        readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        readonly Thread _thread;
        readonly int _frameBytes;
        volatile string _error;
        volatile bool _done;
        public string Error => _error;
        public bool Done => _done;
        /// <summary>아직 보내지 못한 레코드 수 (Host가 뒤처지면 늘어난다).</summary>
        public int Pending => _q.Count;

        public RecordPipe(string name, int width, int height, int fps, bool audio)
        {
            _frameBytes = width * height * 4;
            _pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.None);
            _pipe.Connect(5000);
            _w = new BinaryWriter(_pipe);
            _w.Write((byte)'V'); _w.Write((byte)'P'); _w.Write((byte)'R'); _w.Write((byte)'1');
            _w.Write(width); _w.Write(height); _w.Write(fps); _w.Write(audio ? 1 : 0);
            _thread = new Thread(Run) { IsBackground = true, Name = "RecordPipe" };
            _thread.Start();
        }

        /// <summary>프레임 한 장 크기의 버퍼 (보낸 뒤 돌려받아 다시 쓴다).</summary>
        public byte[] Rent() => _pool.TryTake(out var b) ? b : new byte[_frameBytes];

        public void Frame(byte[] bgra, int count) { if (!_q.IsAddingCompleted) _q.Add((TypeFrame, count, bgra, null, 0)); }
        public void Audio(short[] pcm, int rate) { if (!_q.IsAddingCompleted) _q.Add((TypeAudio, 0, null, pcm, rate)); }

        /// <summary>끝 레코드를 넣고 닫는다 (Done이 될 때까지 기다리면 된다).</summary>
        public void Finish() { if (!_q.IsAddingCompleted) { _q.Add((TypeEnd, 0, null, null, 0)); _q.CompleteAdding(); } }

        /// <summary>끝 레코드 없이 끊는다 (Host는 취소로 본다).</summary>
        public void Abort()
        {
            if (!_q.IsAddingCompleted) _q.CompleteAdding();
            try { _pipe.Dispose(); } catch { }
        }

        void Run()
        {
            try
            {
                foreach (var r in _q.GetConsumingEnumerable())
                {
                    _w.Write(r.Type);
                    if (r.Type == TypeFrame) { _w.Write(r.Count); _w.Write(r.Data, 0, _frameBytes); _pool.Add(r.Data); }
                    else if (r.Type == TypeAudio)
                    {
                        _w.Write(r.Rate); _w.Write(r.Pcm.Length);
                        var b = new byte[r.Pcm.Length * 2];
                        Buffer.BlockCopy(r.Pcm, 0, b, 0, b.Length);
                        _w.Write(b);
                    }
                }
                _w.Flush();
                _pipe.WaitForPipeDrain();
            }
            catch (Exception e) { _error = "녹화 프레임을 보내지 못했어요: " + e.Message; foreach (var _ in _q.GetConsumingEnumerable()) { } }
            finally { try { _pipe.Dispose(); } catch { } _done = true; }
        }
    }

    public sealed class PngWriter
    {
        readonly BlockingCollection<(string[] Paths, byte[] Rgba, int W, int H)> _q = new BlockingCollection<(string[], byte[], int, int)>();
        readonly Thread _thread;
        volatile string _error;
        volatile bool _done;
        public string Error => _error;
        public bool Done => _done;
        public int Pending => _q.Count;

        public PngWriter()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "PngWriter" };
            _thread.Start();
        }

        /// <summary>한 그림을 paths 모두에 (첫 파일만 인코딩하고 나머지는 복사).</summary>
        public void Add(string[] paths, byte[] rgba, int w, int h) { if (!_q.IsAddingCompleted) _q.Add((paths, rgba, w, h)); }
        public void Finish() { if (!_q.IsAddingCompleted) _q.CompleteAdding(); }

        void Run()
        {
            try
            {
                foreach (var it in _q.GetConsumingEnumerable())
                {
                    if (_error != null) continue;
                    File.WriteAllBytes(it.Paths[0], Capture.EncodePng(it.Rgba, it.W, it.H));
                    for (int i = 1; i < it.Paths.Length; i++) File.Copy(it.Paths[0], it.Paths[i], true);
                }
            }
            catch (Exception e) { _error = "PNG를 쓰지 못했어요: " + e.Message; foreach (var _ in _q.GetConsumingEnumerable()) { } }
            finally { _done = true; }
        }
    }

    public static class Wav
    {
        public const int OutRate = 48000;

        /// <summary>16 kHz 소리 → 48 kHz 16비트 (세 배로 늘리며 사이를 직선으로 잇는다 — 말소리에는 충분하다).</summary>
        public static short[] To48k(float[] x)
        {
            if (x.Length == 0) return new short[0];
            var y = new short[x.Length * 3];
            for (int i = 0; i < y.Length; i++)
            {
                int k = i / 3; float t = (i % 3) / 3f;
                float v = x[k] + (x[Math.Min(k + 1, x.Length - 1)] - x[k]) * t;
                y[i] = (short)Math.Max(-32768, Math.Min(32767, (int)Math.Round(v * 32767)));
            }
            return y;
        }

        /// <summary>16비트 모노 PCM WAV.</summary>
        public static void Write(string path, short[] pcm, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = pcm.Length * 2;
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + bytes);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(bytes);
            var b = new byte[bytes];
            Buffer.BlockCopy(pcm, 0, b, 0, bytes);
            w.Write(b);
        }
    }
}
