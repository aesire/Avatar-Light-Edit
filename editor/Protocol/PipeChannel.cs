// named pipe 위의 메시지 채널. 프레임 = 4바이트 리틀 엔디언 길이 + UTF-8 JSON.
// 방향마다 파이프를 따로 쓴다 (<name>.h2u = Host→Unity, <name>.u2h = Unity→Host).
// Windows 동기 파이프 핸들 하나에서 읽기 대기 중이면 쓰기가 막히고, Unity(Mono)의 비동기 파이프 지원은
// 믿기 어렵기 때문이다. 수신은 전용 스레드에서 하고 OnMessage를 그 스레드에서 호출한다.
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VrcProto.Protocol
{
    public sealed class PipeChannel : IDisposable
    {
        const int MaxFrame = 64 * 1024 * 1024;
        readonly Stream _in, _out;
        readonly object _sendLock = new object();
        Thread? _reader;
        int _seq;
        int _closed;

        public event Action<JNode>? OnMessage;
        public event Action<Exception?>? OnClosed;
        public bool IsOpen => _closed == 0;

        public PipeChannel(Stream input, Stream output) { _in = input; _out = output; }

        public static string NewPipeName() => "vrcproto-" + Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>Host 쪽: 두 파이프를 만들고 상대가 접속할 때까지 기다린다.</summary>
        public sealed class Server : IDisposable
        {
            readonly NamedPipeServerStream _h2u, _u2h;
            public Server(string name)
            {
                _h2u = new NamedPipeServerStream(name + ".h2u", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _u2h = new NamedPipeServerStream(name + ".u2h", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            public async Task<PipeChannel> AcceptAsync(CancellationToken ct)
            {
                await Task.WhenAll(_h2u.WaitForConnectionAsync(ct), _u2h.WaitForConnectionAsync(ct)).ConfigureAwait(false);
                return new PipeChannel(_u2h, _h2u);
            }
            public void Dispose() { try { _h2u.Dispose(); } catch { } try { _u2h.Dispose(); } catch { } }
        }

        /// <summary>Unity 쪽: Host가 만든 두 파이프에 접속한다.</summary>
        public static PipeChannel Connect(string name, int timeoutMs)
        {
            var h2u = new NamedPipeClientStream(".", name + ".h2u", PipeDirection.In, PipeOptions.None);
            var u2h = new NamedPipeClientStream(".", name + ".u2h", PipeDirection.Out, PipeOptions.None);
            h2u.Connect(timeoutMs);
            u2h.Connect(timeoutMs);
            return new PipeChannel(h2u, u2h);
        }

        public void Start()
        {
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "PipeChannel.Read" };
            _reader.Start();
        }

        /// <summary>메시지를 보내고 seq를 돌려준다. 여러 스레드에서 호출해도 된다.</summary>
        public int Send(JNode msg)
        {
            if (_closed != 0) return -1;
            int seq = Interlocked.Increment(ref _seq);
            msg.Set("seq", seq);
            var body = Encoding.UTF8.GetBytes(msg.ToJson());
            var frame = new byte[4 + body.Length];
            frame[0] = (byte)body.Length; frame[1] = (byte)(body.Length >> 8);
            frame[2] = (byte)(body.Length >> 16); frame[3] = (byte)(body.Length >> 24);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            try
            {
                lock (_sendLock) { _out.Write(frame, 0, frame.Length); _out.Flush(); }
            }
            catch (Exception e) { Close(e); return -1; }
            return seq;
        }

        void ReadLoop()
        {
            var header = new byte[4];
            try
            {
                while (_closed == 0)
                {
                    if (!ReadExact(header, 4)) break;
                    int len = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
                    if (len < 0 || len > MaxFrame) throw new InvalidDataException("bad frame length " + len);
                    var body = new byte[len];
                    if (!ReadExact(body, len)) break;
                    JNode msg;
                    try { msg = JNode.Parse(Encoding.UTF8.GetString(body)); }
                    catch (FormatException) { continue; } // 깨진 메시지 하나로 채널을 닫지 않음
                    OnMessage?.Invoke(msg);
                }
                Close(null);
            }
            catch (Exception e) { Close(e); }
        }

        bool ReadExact(byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = _in.Read(buf, got, n - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        void Close(Exception? e)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _out.Dispose(); } catch { }
            try { _in.Dispose(); } catch { }
            OnClosed?.Invoke(e);
        }

        public void Dispose() => Close(null);
    }
}
