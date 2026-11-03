// 스튜디오 녹화의 MP4 묶기 (2026-10-19 2차). Windows에 들어 있는 Media Foundation 싱크 라이터(H.264)를 쓴다 — ffmpeg 같은 것을 함께 배포하지 않는다.
// 렌더러가 프레임마다 쓴 raw(위에서 아래로 BGRA)를 받은 순서대로 넣는다. 크기는 짝수여야 한다 (H.264).
// RecordJob: 렌더러의 recordFrame을 받아 백그라운드에서 한 장씩 넣고 raw 파일을 지운다 (UI·파이프를 막지 않게).
//   (2026-10-24) 파이프 녹화: 파일 대신 이름 있는 파이프로 프레임을 바로 받는다 (렌더러 RecordOut.cs RecordPipe — 형식은 거기에). 같은 그림 되풀이·말하기 소리(AAC) 포함.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;

namespace VrcProto.Host.Core
{
    public sealed class Mp4Writer : IDisposable
    {
        readonly int _w, _h, _fps;
        IMFSinkWriter? _writer;
        int _stream, _audioStream = -1;
        long _frame, _audioSamples;
        bool _started;
        public const int AudioRate = 48000;

        // 인코더 미리 준비 (2026-11-01): 프로세스의 첫 Mp4Writer는 Media Foundation·H.264를 올리느라 1초 넘게 걸려
        // 그동안 파이프를 못 읽으면 첫 영상의 앞부분이 멈춘 그림이 된다 (E2E vrlink 첫 영상 7장/37장) → 파이프를 열 때 뒤에서 한 번 만들어 둔다.
        static readonly object WarmLock = new object();
        static System.Threading.Tasks.Task? _warm;

        public static void Warm()
        {
            lock (WarmLock)
                _warm ??= System.Threading.Tasks.Task.Run(() =>
                {
                    var path = Path.Combine(Path.GetTempPath(), "vrcproto_warm_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4");
                    try { using var w = new Mp4Writer(path, 1920, 1080, 30); }
                    catch { }   // 정말 안 되면 실제 녹화가 알린다
                    try { File.Delete(path); } catch { }
                });
        }

        /// <summary>미리 준비가 끝날 때까지 (시작하지 않았으면 바로 true).</summary>
        public static bool WaitWarm(int ms) { lock (WarmLock) if (_warm == null) return true; return _warm.Wait(ms); }

        /// <param name="audio">소리 스트림(AAC 48 kHz 모노 128 kbps)도 넣는다 — WriteAudio로 16비트 PCM을. 하나도 넣지 않으면 마무리할 때 무음.</param>
        public Mp4Writer(string path, int width, int height, int fps, int bitrate = 0, bool audio = false)
        {
            if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("MP4(H.264)는 너비·높이가 짝수여야 해요");
            _w = width; _h = height; _fps = fps;
            if (audio) _audioStream = 0;   // Open에서 정한다
            if (bitrate <= 0) bitrate = (int)Math.Min(100_000_000L, (long)width * height * fps / 8);   // 1080p30 ≈ 7.8Mbps
            Check(MF.MFStartup(0x00020070, 0));
            _started = true;
            var temps = new System.Collections.Generic.List<object>();   // 다 쓰면 놓을 COM 객체 (속성·미디어 형식)
            try { Open(path, bitrate, temps); }
            catch { Dispose(); throw; }   // 만들다 실패해도 MFStartup·싱크 라이터가 새지 않게
            finally { foreach (var o in temps) Marshal.ReleaseComObject(o); }
        }

        void Open(string path, int bitrate, System.Collections.Generic.List<object> temps)
        {
            int width = _w, height = _h, fps = _fps;
            Check(MF.MFCreateAttributes(out var attrs, 2));
            temps.Add(attrs);
            attrs.SetUINT32(MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            if (File.Exists(path)) File.Delete(path);
            Check(MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out var writer));
            _writer = writer;

            Check(MF.MFCreateMediaType(out var outType));
            temps.Add(outType);
            outType.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
            outType.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_H264);
            outType.SetUINT32(MF.MT_AVG_BITRATE, bitrate);
            outType.SetUINT32(MF.MT_INTERLACE_MODE, 2);   // 순차 주사
            outType.SetUINT64(MF.MT_FRAME_SIZE, Pack(width, height));
            outType.SetUINT64(MF.MT_FRAME_RATE, Pack(fps, 1));
            outType.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            Check(writer.AddStream(outType, out _stream));

            Check(MF.MFCreateMediaType(out var inType));
            temps.Add(inType);
            inType.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
            inType.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_RGB32);
            inType.SetUINT32(MF.MT_INTERLACE_MODE, 2);
            inType.SetUINT32(MF.MT_DEFAULT_STRIDE, width * 4);   // 양수 = 위 줄부터
            inType.SetUINT64(MF.MT_FRAME_SIZE, Pack(width, height));
            inType.SetUINT64(MF.MT_FRAME_RATE, Pack(fps, 1));
            inType.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            Check(writer.SetInputMediaType(_stream, inType, null));

            if (_audioStream >= 0)
            {
                // AAC 인코더는 44.1·48 kHz, 평균 12000·16000·20000·24000 바이트/초만 받는다
                Check(MF.MFCreateMediaType(out var aOut));
                temps.Add(aOut);
                aOut.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Audio);
                aOut.SetGUID(MF.MT_SUBTYPE, MF.AudioFormat_AAC);
                aOut.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
                aOut.SetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
                aOut.SetUINT32(MF.MT_AUDIO_NUM_CHANNELS, 1);
                aOut.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, 16000);
                aOut.SetUINT32(MF.MT_AAC_PAYLOAD_TYPE, 0);
                Check(writer.AddStream(aOut, out _audioStream));
                Check(MF.MFCreateMediaType(out var aIn));
                temps.Add(aIn);
                aIn.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Audio);
                aIn.SetGUID(MF.MT_SUBTYPE, MF.AudioFormat_PCM);
                aIn.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
                aIn.SetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
                aIn.SetUINT32(MF.MT_AUDIO_NUM_CHANNELS, 1);
                aIn.SetUINT32(MF.MT_AUDIO_BLOCK_ALIGNMENT, 2);
                aIn.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, AudioRate * 2);
                Check(writer.SetInputMediaType(_audioStream, aIn, null));
            }
            Check(writer.BeginWriting());
        }

        public bool HasAudio => _audioStream >= 0;
        public long AudioSamples => _audioSamples;

        /// <summary>16비트 모노 PCM (48 kHz)을 이어 붙인다. 0.1초씩 나눠 넣는다.</summary>
        public void WriteAudio(short[] pcm)
        {
            if (_writer == null) throw new ObjectDisposedException(nameof(Mp4Writer));
            if (_audioStream < 0) return;
            const int chunk = AudioRate / 10;
            for (int off = 0; off < pcm.Length; off += chunk)
            {
                int n = Math.Min(chunk, pcm.Length - off), len = n * 2;
                Check(MF.MFCreateMemoryBuffer(len, out var buf));
                IMFSample? sample = null;
                try
                {
                    Check(buf.Lock(out var ptr, out _, out _));
                    Marshal.Copy(pcm, off, ptr, n);
                    Check(buf.Unlock());
                    Check(buf.SetCurrentLength(len));
                    Check(MF.MFCreateSample(out sample));
                    Check(sample.AddBuffer(buf));
                    Check(sample.SetSampleTime(_audioSamples * 10_000_000L / AudioRate));
                    Check(sample.SetSampleDuration(n * 10_000_000L / AudioRate));
                    Check(_writer.WriteSample(_audioStream, sample));
                }
                finally
                {
                    if (sample != null) Marshal.ReleaseComObject(sample);
                    Marshal.ReleaseComObject(buf);
                }
                _audioSamples += n;
            }
        }

        public long Frames => _frame;

        /// <summary>위에서 아래로 BGRA 한 장 (너비×높이×4 바이트).</summary>
        public void Write(byte[] bgra)
        {
            if (_writer == null) throw new ObjectDisposedException(nameof(Mp4Writer));
            int len = _w * _h * 4;
            if (bgra.Length < len) throw new ArgumentException($"프레임 크기가 달라요 ({bgra.Length} < {len})");
            Check(MF.MFCreateMemoryBuffer(len, out var buf));
            IMFSample? sample = null;
            try
            {
                Check(buf.Lock(out var ptr, out _, out _));
                Marshal.Copy(bgra, 0, ptr, len);
                Check(buf.Unlock());
                Check(buf.SetCurrentLength(len));
                Check(MF.MFCreateSample(out sample));
                Check(sample.AddBuffer(buf));
                long dur = 10_000_000L / _fps;
                Check(sample.SetSampleTime(_frame * 10_000_000L / _fps));
                Check(sample.SetSampleDuration(dur));
                Check(_writer.WriteSample(_stream, sample));
            }
            finally
            {
                if (sample != null) Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buf);
            }
            _frame++;
        }

        /// <summary>파일을 마무리한다 (이것을 불러야 재생 가능한 MP4가 된다).</summary>
        public void Finish()
        {
            if (_writer == null) return;
            // 소리 스트림을 만들고 아무것도 넣지 않았으면 마무리가 실패하므로 영상 길이만큼 무음
            if (_audioStream >= 0 && _audioSamples == 0) WriteAudio(new short[Math.Max(1, (int)(_frame * AudioRate / Math.Max(1, _fps)))]);
            try { Check(_writer.FinalizeWriting()); }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            if (_writer != null) { Marshal.ReleaseComObject(_writer); _writer = null; }
            if (_started) { MF.MFShutdown(); _started = false; }
        }

        static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;
        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        // ---------------- Media Foundation (필요한 것만) ----------------
        static class MF
        {
            [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFStartup(int version, int flags);
            [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFShutdown();
            [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateAttributes(out IMFAttributes attrs, int size);
            [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out IMFMediaType type);
            [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out IMFSample sample);
            [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(int max, out IMFMediaBuffer buffer);
            [DllImport("mfreadwrite.dll", ExactSpelling = true)]
            public static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attrs, out IMFSinkWriter writer);

            public static readonly Guid MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
            public static readonly Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
            public static readonly Guid MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
            public static readonly Guid MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
            public static readonly Guid MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
            public static readonly Guid MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
            public static readonly Guid MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
            public static readonly Guid MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
            public static readonly Guid READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
            public static readonly Guid MediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
            public static readonly Guid VideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
            public static readonly Guid VideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
            public static readonly Guid MediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
            public static readonly Guid AudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
            public static readonly Guid AudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
            public static readonly Guid MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
            public static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
            public static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
            public static readonly Guid MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
            public static readonly Guid MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
            public static readonly Guid MT_AAC_PAYLOAD_TYPE = new Guid("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        }

        // COM 인터페이스는 vtable 순서대로 모두 적어야 한다 (쓰지 않는 메서드는 자리만: _N).
        // IMFAttributes 메서드 30개: GetItem … CopyAllItems. 쓰는 것은 SetUINT32(18)·SetUINT64(19)·SetGUID(21).
        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFAttributes
        {
            void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
            void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
            void _20();
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
            void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaType
        {
            void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
            void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
            void _20();
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
            void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
            // IMFMediaType: GetMajorType, IsCompressedFormat, IsEqual, GetRepresentation, FreeRepresentation
            void _30(); void _31(); void _32(); void _33(); void _34();
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSample
        {
            void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
            void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
            void _20(); void _21(); void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
            void _GetSampleFlags(); void _SetSampleFlags(); void _GetSampleTime();
            [PreserveSig] int SetSampleTime(long time);
            void _GetSampleDuration();
            [PreserveSig] int SetSampleDuration(long duration);
            void _GetBufferCount(); void _GetBufferByIndex(); void _ConvertToContiguousBuffer();
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
            void _RemoveBufferByIndex(); void _RemoveAllBuffers(); void _GetTotalLength(); void _CopyToBuffer();
        }

        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
            void _GetCurrentLength();
            [PreserveSig] int SetCurrentLength(int length);
            void _GetMaxLength();
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSinkWriter
        {
            [PreserveSig] int AddStream(IMFMediaType type, out int index);
            [PreserveSig] int SetInputMediaType(int index, IMFMediaType type, IMFAttributes? encodingParams);
            [PreserveSig] int BeginWriting();
            [PreserveSig] int WriteSample(int index, IMFSample sample);
            void _SendStreamTick(); void _PlaceMarker(); void _NotifyEndOfSegment(); void _Flush();
            [PreserveSig] int FinalizeWriting();
            void _GetServiceForStream(); void _GetStatistics();
        }
    }

    /// <summary>
    /// 녹화 하나: 렌더러가 프레임 파일을 쓸 때마다 Add, 끝나면 Complete. MP4면 백그라운드 스레드가 raw를 MP4에 넣고 지운다.
    /// PNG 연속 이미지면 할 일이 없다 (렌더러가 고른 폴더에 바로 쓴다).
    /// </summary>
    public sealed class RecordJob
    {
        readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
        readonly Thread? _thread;
        public string? Error { get; private set; }
        public long Encoded => _encoded;
        long _encoded;
        public readonly string Output, FrameFolder;
        public readonly bool Mp4;
        /// <summary>MP4 마무리까지 끝났을 때 (백그라운드 스레드에서 부른다). 인자: 오류 (없으면 null).</summary>
        public Action<string?>? Finished;

        /// <summary>mp4 = true: output은 .mp4 경로, 프레임은 임시 폴더. false: output이 프레임 폴더.</summary>
        public RecordJob(string output, bool mp4, int width, int height, int fps, string? tempFolder = null)
        {
            Output = output; Mp4 = mp4;
            FrameFolder = mp4 ? tempFolder ?? Path.Combine(Path.GetTempPath(), "VrcProto_record_" + Guid.NewGuid().ToString("N").Substring(0, 8)) : output;
            if (!mp4) return;
            Directory.CreateDirectory(FrameFolder);
            _thread = new Thread(() => Run(width, height, fps)) { IsBackground = true, Name = "MP4 녹화" };
            _thread.Start();
        }

        // ---- 파이프 녹화 ----
        /// <summary>파이프 녹화면 렌더러에 보낼 파이프 이름 (record 메시지의 pipe), 아니면 null.</summary>
        public readonly string? PipeName;
        readonly NamedPipeServerStream? _server;
        /// <summary>파이프로 받은 소리 길이 (초).</summary>
        public double AudioSeconds { get; private set; }
        /// <summary>시험용: 파이프로 받은 첫 프레임(BGRA)을 이 파일에도 쓴다.</summary>
        public string? DumpFirstFrame;

        RecordJob(string output)
        {
            Output = output; Mp4 = true; FrameFolder = "";
            Mp4Writer.Warm();
            PipeName = "vrcproto-rec-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 1 << 20, 1 << 20);
            _thread = new Thread(RunPipe) { IsBackground = true, Name = "MP4 녹화 (파이프)" };
            _thread.Start();
        }

        /// <summary>MP4를 파이프로 받는 녹화 (렌더러 record 메시지에 format "pipe", pipe = PipeName). 크기·fps·소리는 렌더러가 머리말로 알린다.</summary>
        public static RecordJob Pipe(string output) => new RecordJob(output);

        public void Add(string framePath) { if (Mp4 && PipeName == null && !_queue.IsAddingCompleted) _queue.Add(framePath); }

        /// <summary>렌더러가 recordDone을 보냈을 때. MP4는 남은 프레임을 넣고 마무리한다 (cancel이면 파일을 지운다).</summary>
        public void Complete(bool cancel = false)
        {
            _cancel = cancel;
            if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
            if (cancel && _server != null) { try { _server.Dispose(); } catch { } }   // 기다리거나 읽던 것을 끊는다
            if (!Mp4) Finished?.Invoke(null);
        }

        void RunPipe()
        {
            Mp4Writer? mp4 = null;
            bool ended = false;
            try
            {
                var s = _server!;
                s.WaitForConnection();
                var r = new BinaryReader(s);
                var magic = r.ReadBytes(4);
                if (magic.Length < 4 || magic[0] != 'V' || magic[1] != 'P' || magic[2] != 'R' || magic[3] != '1') throw new InvalidDataException("녹화 파이프 머리말이 달라요");
                int w = r.ReadInt32(), h = r.ReadInt32(), fps = r.ReadInt32(), flags = r.ReadInt32();
                mp4 = new Mp4Writer(Output, w, h, fps, audio: (flags & 1) != 0);
                var frame = new byte[w * h * 4];
                while (!ended)
                {
                    int type = r.ReadInt32();
                    switch (type)
                    {
                        case 1:
                            int count = r.ReadInt32();
                            ReadFull(s, frame);
                            if (DumpFirstFrame != null && _encoded == 0) File.WriteAllBytes(DumpFirstFrame, frame);
                            for (int i = 0; i < count && !_cancel; i++) { mp4.Write(frame); Interlocked.Increment(ref _encoded); }
                            break;
                        case 2:
                            int rate = r.ReadInt32(), n = r.ReadInt32();
                            var bytes = new byte[n * 2];
                            ReadFull(s, bytes);
                            var pcm = new short[n];
                            Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
                            if (rate == Mp4Writer.AudioRate) { mp4.WriteAudio(pcm); AudioSeconds = (double)mp4.AudioSamples / rate; }
                            break;
                        case 3: ended = true; break;
                        default: throw new InvalidDataException("녹화 파이프 레코드가 다릅니다: " + type);
                    }
                    if (_cancel) break;
                }
                if (ended && !_cancel) { mp4.Finish(); mp4 = null; }
            }
            catch (Exception e) when (e is EndOfStreamException || e is ObjectDisposedException || e is IOException && _cancel) { }   // 끝 레코드 없이 끊김 = 취소
            catch (Exception e) { if (!_cancel) Error = e.Message; }
            finally
            {
                mp4?.Dispose();
                if (mp4 != null || !ended || _cancel) TryDelete(Output);   // 마무리하지 못한 MP4는 재생할 수 없다
                try { _server!.Dispose(); } catch { }
                Finished?.Invoke(Error);
            }
        }

        static void ReadFull(Stream s, byte[] b)
        {
            int off = 0;
            while (off < b.Length)
            {
                int n = s.Read(b, off, b.Length - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }
        volatile bool _cancel;

        /// <summary>시험용: 마무리까지 기다린다 (Finished가 불린 뒤).</summary>
        public bool Wait(int ms) => _thread == null || _thread.Join(ms);

        void Run(int w, int h, int fps)
        {
            Mp4Writer? mp4 = null;
            try
            {
                mp4 = new Mp4Writer(Output, w, h, fps);
                foreach (var f in _queue.GetConsumingEnumerable())
                {
                    if (_cancel) { TryDelete(f); continue; }
                    mp4.Write(File.ReadAllBytes(f));
                    Interlocked.Increment(ref _encoded);
                    TryDelete(f);
                }
                if (_cancel) { mp4.Dispose(); mp4 = null; TryDelete(Output); }
                else { mp4.Finish(); mp4 = null; }
            }
            catch (Exception e) { Error = e.Message; foreach (var f in _queue.GetConsumingEnumerable()) TryDelete(f); }
            finally
            {
                // 마무리하지 못한 MP4는 재생할 수 없으므로 남기지 않는다
                mp4?.Dispose();
                if (Error != null || mp4 != null) TryDelete(Output);
                try { Directory.Delete(FrameFolder, true); } catch { }
                Finished?.Invoke(Error);
            }
        }

        static void TryDelete(string f) { try { File.Delete(f); } catch { } }
    }
}
