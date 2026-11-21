using MirrorFlux.Services.Hardware;

internal static class NdsifShutdownRegression
{
    public static void Run(Action<bool, string> check)
    {
        var output = new BrokenOutput();
        MirrorFlux.Services.Music.AudioOutput.Close(output);
        check(output.DisposeCalled, "Audio device failure during Stop still releases the device");
        var probe = new FakePort();
        new NdsifRegisterLink(probe, "probe").Dispose();
        check(probe.Commands.Count == 0 && !probe.IsOpen, "Detection-only ports are closed without resetting unrelated hardware");

        var port = new FakePort();
        var link = new NdsifRegisterLink(port, "NanoDrive 8");
        check(link.Configure([]), "Test board accepts initial reset");
        link.Start();
        link.Queue(new(0, 0x54, 0, 0, 8, 0x78));
        check(SpinWait.SpinUntil(() => port.RegisterWritten.IsSet, 2000), "Register worker sends key-on before stop");
        link.Dispose(); link.Dispose();
        check(port.Commands.SequenceEqual(new byte[] { 0, 0x54, 0 }) && !port.IsOpen, "Stop sends reset after final register write and closes once");

        var fault = new FakePort { FailRegister = true };
        var broken = new NdsifRegisterLink(fault, "NanoDrive 7");
        check(broken.Configure([]), "Fault test configures board");
        broken.Start(); broken.Queue(new(0, 0x54, 0, 0, 8, 0x78));
        check(SpinWait.SpinUntil(() => !broken.IsConnected, 2000), "Register failure marks board disconnected");
        broken.Dispose();
        check(fault.Commands.Last() == 0 && fault.Commands.Count(c => c == 0) == 2, "A stream fault still sends final hardware reset");

        var waiting = new FakePort { HoldFirstReply = true };
        var pending = new NdsifRegisterLink(waiting, "NanoDrive 8");
        var configure = Task.Run(() => pending.Configure([]));
        check(waiting.Reading.Wait(2000), "Simulated board is waiting for a reply");
        pending.Dispose();
        check(configure.Wait(2000) && !configure.Result && waiting.Commands.SequenceEqual(new byte[] { 0, 0 }), "Stop cancels pending response before sending final reset");
        check(!waiting.IsOpen && !waiting.ReadWhileClosed, "Port stays open until shutdown request completes");
    }

    private sealed class BrokenOutput : NAudio.Wave.IWavePlayer
    {
        public bool DisposeCalled;
        public float Volume { get; set; }
        public NAudio.Wave.PlaybackState PlaybackState => NAudio.Wave.PlaybackState.Stopped;
        public NAudio.Wave.WaveFormat OutputWaveFormat => new();
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(NAudio.Wave.IWaveProvider provider) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() => throw new System.IO.IOException("Device removed");
        public void Dispose() { DisposeCalled = true; throw new System.IO.IOException("Device removed during release"); }
    }

    private sealed class FakePort : INdsifPort
    {
        public string PortName => "TEST";
        public bool IsOpen { get; private set; } = true;
        public readonly List<byte> Commands = [];
        private readonly Queue<byte> _reply = new();
        public readonly ManualResetEventSlim RegisterWritten = new(false), Reading = new(false);
        public bool FailRegister, HoldFirstReply, ReadWhileClosed;
        public void Write(byte[] buffer, int offset, int count)
        {
            if (!IsOpen) throw new InvalidOperationException("Write after close");
            if (!NdsifService.TryParseFrame(buffer.AsSpan(offset, count - 1).ToArray(), out var frame)) throw new Exception("Invalid test frame");
            Commands.Add(frame.CommandId);
            if (frame.MessageType == 0)
            {
                RegisterWritten.Set();
                if (FailRegister) throw new System.IO.IOException("Simulated link failure");
                return;
            }
            if (HoldFirstReply && Commands.Count == 1) return;
            foreach (byte b in NdsifService.BuildFramedPacket(2, frame.CommandId, frame.TransactionId, new byte[] { 0 })) _reply.Enqueue(b);
        }
        public int ReadByte()
        {
            if (!IsOpen) { ReadWhileClosed = true; throw new System.IO.IOException(); }
            if (_reply.TryDequeue(out byte value)) return value;
            Reading.Set(); Thread.Sleep(10); throw new TimeoutException();
        }
        public void Dispose() => IsOpen = false;
    }
}
