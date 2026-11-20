using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using MirrorFlux.Services.Vgm;

namespace MirrorFlux.Services.Hardware;

/// <summary>NdsifRegisterLink のうち、ND8 の OKIM6258 ストリーム（AUDIO_*）を扱う部分。</summary>
internal sealed partial class NdsifRegisterLink
{
    /// <summary>
    /// AUDIO_STATUS の主な値。
    /// 受け付け済み・再生済み・未再生のバイト数と状態フラグ。
    /// </summary>
    private readonly record struct AudioStatus(uint Accepted, uint Played, uint Pending, uint Flags);
    private INd8AudioRenderer? _audioRenderer;
    private NdsifOkiRenderer.Chunk? _heldAudioChunk;
    private readonly Queue<uint> _pendingAudioEvents = new();
    private uint _audioSubmitted;
    private long _audioLastStatusMs;
    private bool _audioPrepared;
    private volatile string _audioDiagnostics = "no status";

    /// <summary>応答を求めないコマンドを送る。</summary>
    private void SendNoReply(byte opcode, ReadOnlySpan<byte> payload)
    {
        byte[] packet = NdsifService.BuildFramedPacket(0, opcode, _transaction++, payload);
        _port.Write(packet, 0, packet.Length);
    }

    /// <summary>
    /// AUDIO_STATUS（0x13）を読み、診断用の文字列を更新する。
    /// 位置が食い違うか ADPCM ストリームの異常フラグが立っていれば例外にする。
    /// </summary>
    private AudioStatus ReadAudioStatus()
    {
        // 応答は40バイト固定で、受け付け・再生・未再生・各種エラー数・フラグが並ぶ。
        byte[] data = Request(0x13, [0xb7, 0]);
        if (data.Length != 40) throw new InvalidDataException("ND8 AUDIO_STATUS length is invalid");
        uint accepted = BinaryPrimitives.ReadUInt32LittleEndian(data), played = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        uint pending = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)), flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
        uint underflows = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        uint overflows = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        uint rejected = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
        uint lateEvents = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32));
        uint maxEventLag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(36));
        _audioDiagnostics = $"accepted={accepted} played={played} pending={pending} underflows={underflows} overflows={overflows} rejected={rejected} late={lateEvents} lag={maxEventLag} flags=0x{flags:X}";
        // 位置の整合と異常フラグ（ビット2）を確かめる。
        if (played > accepted || pending != accepted - played) throw new InvalidDataException("ND8 AUDIO_STATUS positions disagree");
        if ((flags & 4) != 0) throw new IOException($"ND8 ADPCM stream fault: flags=0x{flags:X}");
        _audioPlayed = played;
        return new AudioStatus(accepted, played, pending, flags);
    }

    /// <summary>
    /// AUDIO_EVENT（0x11）を1件送る。
    /// YM2151 書き込み（種類0）は書き込みID 0x54 とインスタンス・ページを前に付ける。
    /// </summary>
    private void SendAudioEvent(NdsifOkiRenderer.AudioEvent audioEvent, ReadOnlySpan<byte> data)
    {
        int extra = audioEvent.Kind == 0 ? 3 : 0;
        byte[] payload = new byte[7 + extra + data.Length];
        payload[0] = 0xb7; payload[1] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), audioEvent.Position);
        payload[6] = audioEvent.Kind;
        if (audioEvent.Kind == 0) { payload[7] = 0x54; payload[8] = 0; payload[9] = 0; }
        data.CopyTo(payload.AsSpan(7 + extra));
        SendNoReply(0x11, payload);
    }

    /// <summary>
    /// 1塊分のイベントと ADPCM データを送る。
    /// 終端マーカーは最後の AUDIO_DATA パケットの後に送る必要がある。
    /// それ以外のイベントは、基板が時刻どおりに反映できるよう音データより先に送る。
    /// </summary>
    private void SendAudioChunk(NdsifOkiRenderer.Chunk chunk, Queue<uint> pendingEvents)
    {
        var events = chunk.Events;
        for (int i = 0; i < events.Length;)
        {
            var current = events[i++];
            // 終端以外のイベントを先に送る。終端（種類3）はここでは飛ばす。
            if (current.Kind == 3) continue;
            // 同じ位置の YM2151 書き込みは、1パケットに収まる範囲でまとめる。
            if (current.Kind == 0)
            {
                var pairs = new List<byte>(current.Payload);
                while (i < events.Length && events[i].Kind == 0 && events[i].Position == current.Position && pairs.Count + 2 <= 246)
                { pairs.AddRange(events[i].Payload); i++; }
                SendAudioEvent(current, pairs.ToArray());
            }
            else SendAudioEvent(current, current.Payload);
            pendingEvents.Enqueue(current.Position);
        }
        // ADPCM データを AUDIO_DATA（0x10）で送る。
        if (chunk.Data.Length != 0)
        {
            byte[] payload = new byte[6 + chunk.Data.Length];
            payload[0] = 0xb7; payload[1] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), chunk.Position);
            chunk.Data.CopyTo(payload, 6);
            SendNoReply(0x10, payload);
        }
        // 最後に終端マーカーを送る。
        foreach (var terminal in events.Where(e => e.Kind == 3))
        {
            SendAudioEvent(terminal, terminal.Payload);
            pendingEvents.Enqueue(terminal.Position);
        }
    }

    /// <summary>
    /// AUDIO_START より前に YM2151 の初期設定を書き込む。
    /// 1パケット254バイトずつに分けて送る。
    /// </summary>
    private void SendInitialYm(INd8AudioRenderer renderer)
    {
        byte[] pairs = renderer.InitialYmWrites();
        for (int at = 0; at < pairs.Length; at += 254)
        {
            int count = Math.Min(254, pairs.Length - at);
            byte[] payload = new byte[2 + count];
            pairs.AsSpan(at, count).CopyTo(payload.AsSpan(2));
            SendNoReply(0x54, payload);
        }
    }

    /// <summary>
    /// 指定位置からストリーム演奏の準備をする。
    /// 基板をリセットし、初期設定と約80ms分の先読みを AUDIO_START より前に送っておく。
    /// </summary>
    internal bool PrepareAudio(long sample)
    {
        try
        {
            // 演奏中なら設定し直してリセットする。
            _audioPrepared = false;
            _audioStage = "configure";
            if (_running && !Configure(_configuredClocks)) return false;
            // リセット後のストリームが空になっているか確かめる。
            _audioStage = "initial status";
            AudioStatus reset = ReadAudioStatus();
            if (reset.Accepted != 0 || reset.Played != 0 || reset.Pending != 0 || reset.Flags != 0)
                throw new InvalidDataException("ND8 ADPCM stream was not cleared by RESET");
            // 作り手を指定位置から作り直し、送信の記録を初期化する。
            _audioRenderer = _audioFactory!(sample);
            _heldAudioChunk = null;
            _pendingAudioEvents.Clear();
            _audioSubmitted = 0;
            _audioPlayed = 0;
            // YM2151 の初期設定を書いてから、約80ms分を先読みとして送る。容量を超えたら失敗とする。
            _audioStage = "prebuffer";
            SendInitialYm(_audioRenderer);
            while (_audioRenderer.TimeUs < 80000 && !_audioRenderer.Finished)
            {
                var chunk = _audioRenderer.Next();
                if (_audioSubmitted + chunk.Data.Length > _audioDataCapacity ||
                    _pendingAudioEvents.Count + chunk.Events.Length > _audioEventCapacity)
                    throw new InvalidDataException("ND8 ADPCM prebuffer exceeds device capacity");
                SendAudioChunk(chunk, _pendingAudioEvents);
                _audioSubmitted = chunk.Position + (uint)chunk.Data.Length;
            }
            // 開始前の応答なし書き込みがすべて届いたことを、応答のあるコマンドで確かめる。
            Request(0x01, []);
            _audioPrepared = true;
            _audioStage = "ready";
            return true;
        }
        catch (Exception ex)
        {
            // 失敗したら切断扱いにし、段階表示に理由を残す。
            _connected = false;
            _audioStage = ex.GetType().Name + ": " + ex.Message;
            Debug.WriteLine($"[NDSIF] ND8 audio stream stopped: {ex.Message}");
            return false;
        }
    }

    /// <summary>AUDIO_START（0x12）で初期クロックと分周を送り、演奏時計を動かし始める。</summary>
    private void StartAudioStream()
    {
        try
        {
            if (_audioRenderer == null || !_audioPrepared) return;
            byte[] start = new byte[8]; start[0] = 0xb7;
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(2), _audioRenderer.InitialClock);
            BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(6), _audioRenderer.InitialDivider);
            SendNoReply(0x12, start);
            _clock.Restart(); _audioLastStatusMs = -20;
            _audioStage = "running";
        }
        catch (Exception ex)
        {
            _connected = false;
            _audioStage = ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// ストリーム演奏の送信スレッド。
    /// 状態を20msごとに読み、約80ms先まで塊を送り続ける。
    /// </summary>
    private void WorkerAudio()
    {
        try
        {
            while (_running && _connected)
            {
                // 一時停止になったら、基板の FIFO をすぐ止めて送信の記録を捨てる。
                if (_paused)
                {
                    if (!_audioStopped.IsSet)
                    {
                        Request(0x00, []);
                        _audioPrepared = false;
                        _audioRenderer = null;
                        _heldAudioChunk = null;
                        _pendingAudioEvents.Clear();
                        _audioSubmitted = _audioPlayed = 0;
                        _audioStopped.Set();
                    }
                    _signal.WaitOne(2);
                    continue;
                }
                // 20msごとに状態を読み、再生済みのイベントを待ち行列から外す。
                long elapsedMs = _clock.ElapsedMilliseconds;
                if (elapsedMs - _audioLastStatusMs >= 20)
                {
                    AudioStatus status = ReadAudioStatus();
                    while (_pendingAudioEvents.Count != 0 && _pendingAudioEvents.Peek() < status.Played)
                        _pendingAudioEvents.Dequeue();
                    _audioLastStatusMs = elapsedMs;
                }
                if (_audioRenderer == null) { _signal.WaitOne(1); continue; }
                // 約80ms先まで、基板の容量に収まる限り塊を送る。収まらない塊は次回まで持ち越す。
                bool sent = false;
                while (_running && !_paused && !_audioRenderer.Finished &&
                    _audioRenderer.TimeUs < (ulong)(elapsedMs + 80) * 1000)
                {
                    _heldAudioChunk ??= _audioRenderer.Next();
                    var chunk = _heldAudioChunk.Value;
                    if ((ulong)_audioSubmitted - _audioPlayed + (uint)chunk.Data.Length > _audioDataCapacity ||
                        _pendingAudioEvents.Count + chunk.Events.Length > _audioEventCapacity) break;
                    SendAudioChunk(chunk, _pendingAudioEvents);
                    _audioSubmitted = chunk.Position + (uint)chunk.Data.Length;
                    _heldAudioChunk = null; sent = true;
                }
                if (!sent) _signal.WaitOne(1);
            }
        }
        catch (Exception ex)
        {
            // 例外で止まったら切断扱いにし、一時停止の待ち手も起こす。
            _connected = false;
            _audioStage = ex.GetType().Name + ": " + ex.Message;
            _audioStopped.Set();
            Debug.WriteLine($"[NDSIF] ND8 audio stream stopped: {ex.Message}");
        }
    }
}
