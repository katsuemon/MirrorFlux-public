using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MirrorFlux.Services.Hardware;

/// <summary>
/// NdsifService のうち、送信スレッドで NanoDrive 6 へ書き込みを送る部分。
/// Timed DAC Stream（0x18〜0x1D）を使う時刻付き送信と、使えない基板向けの即時送信を持つ。
/// </summary>
public sealed partial class NdsifService
{
    /// <summary>
    /// 1枠分のパケットを作り、まとめ送り用のバッファに足す。
    /// 足りなければバッファを広げる。
    /// </summary>
    private void SendImmediateFrameToBuffer(byte messageType, byte cmd, ushort txId, ReadOnlySpan<byte> payload, ref byte[] streamBuffer, ref int streamBufferPos)
    {
        byte[] framed = BuildFramedPacket(messageType, cmd, txId, payload);
        if (streamBufferPos + framed.Length > streamBuffer.Length)
        {
            Array.Resize(ref streamBuffer, Math.Max(streamBuffer.Length * 2, streamBufferPos + framed.Length));
        }
        Buffer.BlockCopy(framed, 0, streamBuffer, streamBufferPos, framed.Length);
        streamBufferPos += framed.Length;
    }

    /// <summary>
    /// まとめ送り用のバッファをポートへ書き出して空にする。
    /// 失敗したら切断扱いにする。
    /// </summary>
    private void FlushStreamBuffer(byte[] streamBuffer, ref int streamBufferPos)
    {
        if (streamBufferPos <= 0) return;
        try
        {
            lock (_lock)
            {
                _activeStream?.Write(streamBuffer, 0, streamBufferPos);
            }
        }
        catch (Exception ex)
        {
            MarkLinkLost("TX write", ex);
        }
        streamBufferPos = 0;
    }

    /// <summary>
    /// 控えておいた YM2612 のレジスタ値を基板へ書き戻す。
    /// シークで DAC ストリームをリセットした後に音色を元に戻すために使う。
    /// </summary>
    private void RestoreYmStateImmediate(ref byte[] streamBuffer, ref int streamBufferPos)
    {
        Span<byte> payload = stackalloc byte[2 + 60 * 2];
        // ポートごとに 0x20〜0xB6 の値を最大60組ずつ送る。
        for (byte port = 0; port < 2; port++)
        {
            byte cmd = (byte)(port == 0 ? 0x52 : 0x53);
            int count = 0;
            payload[0] = 0;
            payload[1] = port;

            for (int reg = 0x20; reg <= 0xB6; reg++)
            {
                // キーオン（0x28）と DAC のサンプル値（0x2A）は書き戻さない。ポート1は 0x30 未満を持たない。値が0のものは、DAC 有効（0x2B）と 0xB0 以降を除いて省く。
                if (port == 0 && reg is 0x28 or 0x2A) continue;
                if (port == 1 && reg < 0x30) continue;
                byte val = _ymRegs[port, reg];
                if (val == 0 && reg != 0x2B && reg < 0xB0) continue;

                payload[2 + count * 2] = (byte)reg;
                payload[3 + count * 2] = val;
                count++;

                if (count == 60)
                {
                    SendImmediateFrameToBuffer(0x00, cmd, 0, payload.Slice(0, 2 + count * 2), ref streamBuffer, ref streamBufferPos);
                    count = 0;
                }
            }

            if (count > 0)
            {
                SendImmediateFrameToBuffer(0x00, cmd, 0, payload.Slice(0, 2 + count * 2), ref streamBuffer, ref streamBufferPos);
            }
        }
        FlushStreamBuffer(streamBuffer, ref streamBufferPos);
    }

    /// <summary>
    /// 送信スレッドの入口。
    /// 送信スレッドの未処理例外はアプリ全体を終了させるので、機器が抜かれた・応答しない場合の書き込み失敗は切断として扱い、スレッドを静かに終える。
    /// 次の曲の開始時に再検出する。
    /// </summary>
    private void TxWorkerLoop()
    {
        try { TxWorkerLoopCore(); }
        catch (Exception ex)
        {
            MarkLinkLost("TX worker", ex);
            _txRunning = false;
            while (_txQueue.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// 送信スレッドの本体。
    /// Timed DAC Stream では DAC レコードとレジスタイベントを基板の容量に合わせて先送りし、状態を読んで時計を基板に合わせる。
    /// </summary>
    private void TxWorkerLoopCore()
    {
        // 作業用のバッファと、送信待ち・送信済みの見積もりを置く待ち行列を用意する。
        Span<byte> payloadBuffer = stackalloc byte[2 + 60 * 2];
        Span<(byte reg, byte val)> ymBatch = stackalloc (byte reg, byte val)[60];
        Span<byte> psgBatch = stackalloc byte[120];
        Span<DacRecord> dacRecords = stackalloc DacRecord[60];
        byte[] streamBuffer = new byte[4096];
        byte[] rxScratch = new byte[1024];
        var rxAssembler = new List<byte>(256);

        var pendingEvents = new Queue<NdsifWrite>(1024);
        var pendingDacOps = new Queue<(ulong Tick, byte Kind, byte Val)>(2048);
        var inFlightRecords = new Queue<(ulong EndTick, int Count)>(256);
        var inFlightEvents = new Queue<(ulong Tick, int Count)>(512);

        // ストリームの状態と、基板側に溜まっている量の見積もり。
        int localGen = _sessionGeneration;
        bool dacStreamOpen = false;
        bool dacStartSent = false;
        bool dacEndSent = false;
        uint dacSequence = 0;
        ulong dacPosition = 0;
        ulong lastSentEventTick = 0;
        ulong streamBaseSample = 0;
        ulong lastStatusQueryMs = 0;
        bool statusRequestInFlight = false;
        uint totalEventsSent = 0;
        uint estimatedDeviceQueuedRecords = 0;
        uint estimatedDeviceQueuedEvents = 0;
        ulong anchorPlayedTick = 0;
        long anchorStopwatchTicks = 0;

        while (_txRunning)
        {
            Interlocked.Exchange(ref _txSignaled, 0);

            // 切断中は送信待ちを捨てて待つ。
            if (!IsConnected || _activeStream == null)
            {
                while (_txQueue.TryDequeue(out _)) { }
                pendingEvents.Clear();
                pendingDacOps.Clear();
                inFlightRecords.Clear();
                inFlightEvents.Clear();
                rxAssembler.Clear();
                _txSignal.WaitOne(10);
                continue;
            }

            // 新しいセッション（リセットやシーク）になったら、送信の記録をすべて初期化する。
            if (localGen != _sessionGeneration)
            {
                bool cleanReset;
                lock (_lock)
                {
                    localGen = _sessionGeneration;
                    cleanReset = _isCleanReset;
                    _isCleanReset = false;
                    streamBaseSample = _baseSampleOffset;
                }

                pendingEvents.Clear();
                pendingDacOps.Clear();
                inFlightRecords.Clear();
                inFlightEvents.Clear();
                rxAssembler.Clear();
                dacSequence = 0;
                dacPosition = 0;
                lastSentEventTick = 0;
                dacStartSent = false;
                dacEndSent = false;
                statusRequestInFlight = false;
                lastStatusQueryMs = 0;
                totalEventsSent = 0;
                estimatedDeviceQueuedRecords = 0;
                estimatedDeviceQueuedEvents = 0;
                anchorPlayedTick = 0;
                anchorStopwatchTicks = 0;

                // リセットを伴わない途中シーク（Reset なしの ResetClock）では、基板の DAC ストリームをリセットして YM2612 の音色を書き戻す。
                if (dacStreamOpen && !cleanReset)
                {
                    try
                    {
                        lock (_lock)
                        {
                            if (_activeStream != null)
                            {
                                if (_serialPort != null) _serialPort.DiscardInBuffer();
                                byte[] resetPacket = BuildFramedPacket(0x01, 0x00, _transactionCounter++, ReadOnlySpan<byte>.Empty);
                                _activeStream.Write(resetPacket, 0, resetPacket.Length);
                                _activeStream.Flush();
                                if (_serialPort != null)
                                {
                                    TryReadFrame(_activeStream, 80, out _);
                                }
                            }
                        }
                        int restorePos = 0;
                        RestoreYmStateImmediate(ref streamBuffer, ref restorePos);
                    }
                    catch { }
                }
                dacStreamOpen = false;
            }

            // Timed DAC に対応しない基板は即時送信で送る。
            if (!SupportsTimedDac)
            {
                RunImmediateTxStep(payloadBuffer, ymBatch, psgBatch, ref streamBuffer);
                continue;
            }

            // NanoDrive 6 の 512 バイトの送信バッファが詰まらないよう、受信したバイトは毎回待たずに読み出す。
            if (_serialPort != null)
            {
                DrainSerialRepliesNonBlocking(
                    rxScratch,
                    rxAssembler,
                    ref dacStreamOpen,
                    ref dacStartSent,
                    ref dacEndSent,
                    ref dacSequence,
                    ref dacPosition,
                    ref lastSentEventTick,
                    ref streamBaseSample,
                    ref statusRequestInFlight,
                    ref totalEventsSent,
                    ref estimatedDeviceQueuedRecords,
                    ref estimatedDeviceQueuedEvents,
                    ref anchorPlayedTick,
                    ref anchorStopwatchTicks,
                    inFlightRecords,
                    inFlightEvents,
                    pendingEvents,
                    pendingDacOps,
                    ref streamBuffer);
            }

            // 時計の開始前や一時停止中は待つ。
            if (!_clockRequested || _isPaused)
            {
                _txSignal.WaitOne(5);
                continue;
            }

            int streamBufferPos = 0;

            // _txQueue を取り出す前に _parsedSample と _sourceEnded を控える。こうすると TargetSample が parsedAbs 以下の書き込みは、取り出す時点で必ず _txQueue に入っている。
            bool sourceEndedSnap = _sourceEnded;
            uint parsedAbs = Volatile.Read(ref _parsedSample);

            // 1. _txQueue を pendingDacOps と pendingEvents へ振り分ける。ストリームがまっさらなら、ティック0の書き込みをまとめて即時に送る。
            if (!dacStreamOpen && dacSequence == 0 && dacPosition == 0)
            {
                streamBaseSample = _baseSampleOffset;

                // ND6 の DacStream がまっさらな間に、ティック0の初期設定をまとめてすぐ送る。DAC 値（0x2A）と DAC 有効（0x2B）は最後の値だけ DAC レコードに回す。
                bool hasTick0Dac = false;
                byte tick0DacVal = 0x80;
                bool hasTick0Select = false;
                byte tick0SelectVal = 0x00;

                while (_txQueue.TryPeek(out var peek0) && peek0.TargetSample <= streamBaseSample)
                {
                    // DAC 値と DAC 有効は最後の値だけ覚える。
                    if (peek0.Cmd == 0x52 && peek0.Port == 0 && peek0.Reg is 0x2A or 0x2B)
                    {
                        _txQueue.TryDequeue(out var item0);
                        _ymRegs[0, item0.Reg] = item0.Val;
                        if (item0.Reg == 0x2A)
                        {
                            hasTick0Dac = true;
                            tick0DacVal = item0.Val;
                        }
                        else
                        {
                            hasTick0Select = true;
                            tick0SelectVal = (byte)(item0.Val & 0x80);
                        }
                    }
                    else if (peek0.Cmd is 0x52 or 0x53)
                    {
                        // YM2612 の書き込みは同じポートの続きを最大60組まとめて送る。
                        byte currentCmd = peek0.Cmd;
                        byte currentPort = peek0.Port;
                        int count = 0;

                        while (count < 60 && _txQueue.TryPeek(out var item) &&
                               item.TargetSample <= streamBaseSample &&
                               item.Cmd == currentCmd && item.Port == currentPort &&
                               !(item.Cmd == 0x52 && item.Port == 0 && item.Reg is 0x2A or 0x2B))
                        {
                            _txQueue.TryDequeue(out item);
                            _ymRegs[currentPort, item.Reg] = item.Val;
                            ymBatch[count++] = (item.Reg, item.Val);
                        }

                        if (count > 0)
                        {
                            payloadBuffer[0] = 0;
                            payloadBuffer[1] = currentPort;
                            for (int i = 0; i < count; i++)
                            {
                                payloadBuffer[2 + i * 2] = ymBatch[i].reg;
                                payloadBuffer[3 + i * 2] = ymBatch[i].val;
                            }
                            SendImmediateFrameToBuffer(0x00, currentCmd, 0, payloadBuffer.Slice(0, 2 + count * 2), ref streamBuffer, ref streamBufferPos);
                        }
                    }
                    else if (peek0.Cmd is 0x50 or 0x30)
                    {
                        // SN76489 の書き込みは同じ番号の続きを最大120バイトまとめて送る。
                        byte currentCmd = peek0.Cmd;
                        byte currentInstance = peek0.Port;
                        int count = 0;

                        while (count < 120 && _txQueue.TryPeek(out var item) &&
                               item.TargetSample <= streamBaseSample &&
                               item.Cmd == currentCmd && item.Port == currentInstance)
                        {
                            _txQueue.TryDequeue(out item);
                            psgBatch[count++] = item.Val;
                        }

                        if (count > 0)
                        {
                            payloadBuffer[0] = currentInstance;
                            payloadBuffer[1] = 0;
                            for (int i = 0; i < count; i++)
                            {
                                payloadBuffer[2 + i] = psgBatch[i];
                            }
                            SendImmediateFrameToBuffer(0x00, currentCmd, 0, payloadBuffer.Slice(0, 2 + count), ref streamBuffer, ref streamBufferPos);
                        }
                    }
                    else
                    {
                        _txQueue.TryDequeue(out _);
                    }
                }

                // ティック0の DAC 有効と DAC 値を DAC レコードの先頭に積む。
                if (hasTick0Select)
                {
                    pendingDacOps.Enqueue((0, DacRecord.KindSelect, tick0SelectVal));
                }
                if (hasTick0Dac)
                {
                    pendingDacOps.Enqueue((0, DacRecord.KindDac, tick0DacVal));
                }

                FlushStreamBuffer(streamBuffer, ref streamBufferPos);
            }

            // 残りの書き込みを、送信位置より前にならないよう時刻を詰めて振り分ける。DAC 値と DAC 有効は DAC レコード、それ以外はレジスタイベントにする。
            while (_txQueue.TryDequeue(out var w))
            {
                if (w.Cmd is 0x52 or 0x53)
                {
                    _ymRegs[w.Port, w.Reg] = w.Val;
                }

                ulong relTick = w.TargetSample > streamBaseSample ? (ulong)w.TargetSample - streamBaseSample : 0;
                if (relTick < dacPosition)
                {
                    relTick = dacPosition;
                }

                if (w.Cmd == 0x52 && w.Port == 0 && w.Reg == 0x2A)
                {
                    pendingDacOps.Enqueue((relTick, DacRecord.KindDac, w.Val));
                }
                else if (w.Cmd == 0x52 && w.Port == 0 && w.Reg == 0x2B)
                {
                    pendingDacOps.Enqueue((relTick, DacRecord.KindSelect, (byte)(w.Val & 0x80)));
                }
                else
                {
                    uint adjSample = (uint)(streamBaseSample + relTick);
                    pendingEvents.Enqueue(new NdsifWrite(adjSample, w.Cmd, w.Port, w.Reg, w.Val));
                }
            }

            // 2. いまの再生ティックを求める。0x1C の状態が届いていれば ND6 の時計に合わせる。
            ulong parsedHorizon = parsedAbs > streamBaseSample ? (ulong)parsedAbs - streamBaseSample : 0;

            ulong nowRelSample = 0;
            if (dacStartSent && _playbackClock.IsRunning)
            {
                long elapsedAfterAnchor = _playbackClock.ElapsedTicks - anchorStopwatchTicks;
                if (elapsedAfterAnchor < 0) elapsedAfterAnchor = 0;
                nowRelSample = anchorPlayedTick + (ulong)((double)elapsedAfterAnchor / Stopwatch.Frequency * 44100.0);
            }

            // 再生済みと見なせる分を、基板に溜まっている量の見積もりから引く。
            while (inFlightRecords.TryPeek(out var recFront) && recFront.EndTick <= nowRelSample)
            {
                inFlightRecords.Dequeue();
                estimatedDeviceQueuedRecords = estimatedDeviceQueuedRecords > (uint)recFront.Count
                    ? estimatedDeviceQueuedRecords - (uint)recFront.Count
                    : 0;
            }
            while (inFlightEvents.TryPeek(out var evFront) && evFront.Tick <= nowRelSample)
            {
                inFlightEvents.Dequeue();
                estimatedDeviceQueuedEvents = estimatedDeviceQueuedEvents > (uint)evFront.Count
                    ? estimatedDeviceQueuedEvents - (uint)evFront.Count
                    : 0;
            }

            // NanoDrive 6 には再生位置より約6000ティック（約136ms）先まで送っておく。
            const ulong TargetLeadTicks = 6000;
            ulong desiredHorizon = Math.Min(parsedHorizon, nowRelSample + TargetLeadTicks);

            // アンダーフロー防止の余裕。DAC ストリームの再生中に WaveOut のコールバックが遅れ、parsedHorizon が nowRelSample の1800ティック（約40ms）以内まで迫ったら、待ちレコードで desiredHorizon を延ばす。NanoDrive 6 の records_ リングバッファが空（count_ == 0）になると fail(Underflow) から FM.reset() されるのを防ぐ。
            if (dacStartSent && !sourceEndedSnap && desiredHorizon < nowRelSample + 1800)
            {
                desiredHorizon = nowRelSample + 2400;
            }

            // 1回の周回で最大16パケットまで、基板のレコード容量に余裕がある間だけ塊を作って送る。
            uint maxRecPerPacket = _dacMaxRecords > 0 ? _dacMaxRecords : 60u;
            int packetsSentThisTurn = 0;

            while (packetsSentThisTurn < 16 &&
                   (dacPosition < desiredHorizon || (sourceEndedSnap && dacPosition >= parsedHorizon && (pendingDacOps.Count > 0 || pendingEvents.Count > 0))) &&
                   estimatedDeviceQueuedRecords + maxRecPerPacket <= _dacRecordCapacity - 256)
            {
                // ND6 のイベント待ち行列（容量512）が埋まりかけていて、DAC の先行分が十分あるときは、ND6 が期限の来たイベントを消化するまで新しい塊を足さない。
                if (estimatedDeviceQueuedEvents >= (uint)(_dacEventCapacity - 96) &&
                    dacStartSent &&
                    dacPosition > nowRelSample + 1500 &&
                    !sourceEndedSnap)
                {
                    break;
                }

                // DAC レコードを1パケット分（最大 maxRecPerPacket 件）組み立てる。
                int recCount = 0;
                ulong cursor = dacPosition;

                while (recCount < maxRecPerPacket)
                {
                    if (cursor >= desiredHorizon && !(sourceEndedSnap && cursor >= parsedHorizon && (pendingDacOps.Count > 0 || pendingEvents.Count > 0)))
                    {
                        break;
                    }

                    if (pendingDacOps.TryPeek(out var nextOp))
                    {
                        ulong opTick = nextOp.Tick < cursor ? cursor : nextOp.Tick;
                        if (opTick > cursor)
                        {
                            // 次の操作（または desiredHorizon）までの隙間を待ちレコードで埋める。
                            ulong targetTick = !sourceEndedSnap && opTick > desiredHorizon ? desiredHorizon : opTick;
                            if (targetTick <= cursor)
                                break;
                            ulong gap = targetTick - cursor;
                            ushort waitTicks = (ushort)Math.Min(gap, 4096UL);
                            dacRecords[recCount++] = new DacRecord(DacRecord.KindWait, 0, waitTicks);
                            cursor += waitTicks;
                            continue;
                        }

                        // opTick == cursor なのでこの DAC 操作を取り出す。同じティックに続く DAC 値の書き込みは最後の1つにまとめる。
                        pendingDacOps.Dequeue();
                        while (nextOp.Kind == DacRecord.KindDac &&
                               pendingDacOps.TryPeek(out var sameTickOp) &&
                               sameTickOp.Kind == DacRecord.KindDac &&
                               sameTickOp.Tick <= cursor)
                        {
                            nextOp = pendingDacOps.Dequeue();
                        }

                        // 次の操作までの待ちをこのレコードに持たせる。
                        ulong nextTickAfter;
                        if (pendingDacOps.TryPeek(out var followingOp))
                        {
                            ulong followTick = followingOp.Tick < cursor ? cursor : followingOp.Tick;
                            nextTickAfter = !sourceEndedSnap && followTick > desiredHorizon ? desiredHorizon : followTick;
                        }
                        else if (cursor < desiredHorizon)
                        {
                            nextTickAfter = desiredHorizon;
                        }
                        else
                        {
                            nextTickAfter = cursor + 1;
                        }

                        ulong delta = nextTickAfter > cursor ? nextTickAfter - cursor : 0;
                        ushort stepWait = (ushort)Math.Min(delta, 4096UL);
                        dacRecords[recCount++] = new DacRecord(nextOp.Kind, nextOp.Val, stepWait);
                        cursor += stepWait;
                    }
                    else
                    {
                        // DAC 操作が残っていないときは、待ちレコードで desiredHorizon まで進める。曲の終わりでイベントだけ残っていれば、1ティックの待ちを送ってイベントを流す。
                        if (cursor < desiredHorizon)
                        {
                            ulong gap = desiredHorizon - cursor;
                            ushort waitTicks = (ushort)Math.Min(gap, 1024UL);
                            dacRecords[recCount++] = new DacRecord(DacRecord.KindWait, 0, waitTicks);
                            cursor += waitTicks;
                        }
                        else if (sourceEndedSnap && cursor >= parsedHorizon && pendingEvents.Count > 0 && recCount == 0)
                        {
                            dacRecords[recCount++] = new DacRecord(DacRecord.KindWait, 0, 1);
                            cursor += 1;
                            break;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                if (recCount == 0)
                    break;

                ulong chunkEndTick = cursor;

                // 2a. [dacPosition, chunkEndTick) の FM/PSG の REGISTER_EVENT（0x1D）を、この塊の DAC_DATA（0x19）より先にすべて送る。ND6 の前提として、evTick >= dacPosition（acceptedTick_）、evTick >= lastSentEventTick（lastEventTick_）、eventCount_ + count <= EVENT_CAPACITY（512）を守る。
                while (pendingEvents.TryPeek(out var evPeek))
                {
                    ulong evTick = evPeek.TargetSample > streamBaseSample ? (ulong)evPeek.TargetSample - streamBaseSample : 0;
                    if (evTick < dacPosition) evTick = dacPosition;
                    if (evTick < lastSentEventTick) evTick = lastSentEventTick;
                    if (evTick >= chunkEndTick && !(chunkEndTick == dacPosition && evTick == dacPosition))
                        break;

                    // イベント領域が埋まりかけたら、塊の中のイベントを1つにまとめるか、急がないイベントを後回しにする。
                    bool coalesceChunkEvents = estimatedDeviceQueuedEvents >= (uint)(_dacEventCapacity - 192);
                    int maxEventsAllowed = (int)_dacEventCapacity - (int)estimatedDeviceQueuedEvents - 16;
                    // いまは ND6 の512件のイベント領域にこれ以上入らない。残りは pendingEvents に置いておく（次の塊の dacPosition へ詰められる）。
                    if (maxEventsAllowed <= 0)
                    {
                        break;
                    }

                    byte evCmd = evPeek.Cmd;
                    byte evPort = evPeek.Port;

                    // YM2612 のイベントを同じティック（まとめるときは塊の範囲）・同じポートの分だけ集める。
                    if (evCmd is 0x52 or 0x53)
                    {
                        int count = 0;
                        int batchLimit = Math.Min(60, maxEventsAllowed);
                        while (count < batchLimit && pendingEvents.TryPeek(out var item))
                        {
                            ulong itemTick = item.TargetSample > streamBaseSample ? (ulong)item.TargetSample - streamBaseSample : 0;
                            if (itemTick < dacPosition) itemTick = dacPosition;
                            if (itemTick < lastSentEventTick) itemTick = lastSentEventTick;

                            bool tickMatch = coalesceChunkEvents
                                ? (itemTick < chunkEndTick || (chunkEndTick == dacPosition && itemTick == dacPosition))
                                : (itemTick == evTick);
                            if (!tickMatch || item.Cmd != evCmd || item.Port != evPort)
                                break;

                            pendingEvents.Dequeue();

                            // 同じ組の中で同じ YM2612 レジスタへの書き込みは、キーオン・オフ（0x28）を除いて最後の値にまとめる。
                            bool merged = false;
                            if (!(evPort == 0 && item.Reg == 0x28))
                            {
                                for (int k = 0; k < count; k++)
                                {
                                    if (ymBatch[k].reg == item.Reg)
                                    {
                                        ymBatch[k] = (item.Reg, item.Val);
                                        merged = true;
                                        break;
                                    }
                                }
                            }
                            if (!merged)
                            {
                                ymBatch[count++] = (item.Reg, item.Val);
                            }
                        }

                        // 集めた組を1つの REGISTER_EVENT にして送り、見積もりに足す。
                        if (count > 0)
                        {
                            Span<byte> regData = payloadBuffer.Slice(0, count * 2);
                            for (int i = 0; i < count; i++)
                            {
                                regData[i * 2] = ymBatch[i].reg;
                                regData[i * 2 + 1] = ymBatch[i].val;
                            }
                            byte[] evPayload = BuildRegisterEventPayload(evTick, evCmd, 0, evPort, regData);
                            SendImmediateFrameToBuffer(0x00, 0x1D, 0, evPayload, ref streamBuffer, ref streamBufferPos);
                            lastSentEventTick = evTick;
                            totalEventsSent += (uint)count;
                            estimatedDeviceQueuedEvents += (uint)count;
                            inFlightEvents.Enqueue((evTick, count));
                        }
                    }
                    else if (evCmd is 0x50 or 0x30)
                    {
                        // SN76489 のイベントも同じ条件で集めて送る。
                        int count = 0;
                        int batchLimit = Math.Min(120, maxEventsAllowed);
                        while (count < batchLimit && pendingEvents.TryPeek(out var item))
                        {
                            ulong itemTick = item.TargetSample > streamBaseSample ? (ulong)item.TargetSample - streamBaseSample : 0;
                            if (itemTick < dacPosition) itemTick = dacPosition;
                            if (itemTick < lastSentEventTick) itemTick = lastSentEventTick;

                            bool tickMatch = coalesceChunkEvents
                                ? (itemTick < chunkEndTick || (chunkEndTick == dacPosition && itemTick == dacPosition))
                                : (itemTick == evTick);
                            if (!tickMatch || item.Cmd != evCmd || item.Port != evPort)
                                break;

                            pendingEvents.Dequeue();
                            psgBatch[count++] = item.Val;
                        }

                        if (count > 0)
                        {
                            byte[] evPayload = BuildRegisterEventPayload(evTick, evCmd, evPort, 0, psgBatch.Slice(0, count));
                            SendImmediateFrameToBuffer(0x00, 0x1D, 0, evPayload, ref streamBuffer, ref streamBufferPos);
                            lastSentEventTick = evTick;
                            totalEventsSent += (uint)count;
                            estimatedDeviceQueuedEvents += (uint)count;
                            inFlightEvents.Enqueue((evTick, count));
                        }
                    }
                    else
                    {
                        pendingEvents.Dequeue();
                    }
                }

                // 2b. この塊の DAC_DATA（0x19）を送る。
                byte[] dacPayload = BuildDacDataPayload(dacSequence, dacPosition, dacRecords.Slice(0, recCount));
                SendImmediateFrameToBuffer(0x00, 0x19, 0, dacPayload, ref streamBuffer, ref streamBufferPos);

                dacSequence += (uint)recCount;
                dacPosition = chunkEndTick;
                dacStreamOpen = true;
                estimatedDeviceQueuedRecords += (uint)recCount;
                inFlightRecords.Enqueue((chunkEndTick, recCount));
                packetsSentThisTurn++;

                // まだ再生を始めていないのにイベント領域（512件）が65%を超えたら、ここで抜けてすぐ DAC_START を送り、ND6 に events_[512] を消化させる。
                if (!dacStartSent && dacPosition > 0 && estimatedDeviceQueuedEvents >= (uint)(_dacEventCapacity - 160))
                {
                    break;
                }
            }

            FlushStreamBuffer(streamBuffer, ref streamBufferPos);

            // 3. 先読みが閾値に達したら（またはイベント領域を空ける必要があるか、曲が終わったら）DAC_START（0x1A）を1回送り、演奏時計を動かす。
            if (dacStreamOpen && !dacStartSent &&
                (dacPosition >= 2000 || _sourceEnded || (dacPosition > 0 && estimatedDeviceQueuedEvents >= (uint)(_dacEventCapacity - 160))))
            {
                uint leadTicks = (uint)Math.Clamp(dacPosition, 1UL, 2000UL);
                byte[] startPayload = BuildDacStartPayload(leadTicks);
                ushort txId = _transactionCounter++;
                byte[] startPacket = BuildFramedPacket(0x01, 0x1A, txId, startPayload);

                lock (_lock)
                {
                    if (_activeStream != null)
                    {
                        _activeStream.Write(startPacket, 0, startPacket.Length);
                        _activeStream.Flush();
                        if (_serialPort != null)
                        {
                            TryReadFrame(_activeStream, 80, out _);
                        }
                    }
                }

                _playbackClock.Restart();
                anchorPlayedTick = 0;
                anchorStopwatchTicks = 0;
                dacStartSent = true;
                lastStatusQueryMs = 0;
                statusRequestInFlight = false;
            }

            // 4. 曲が終わり、イベントとレコードをすべて送り終えたら DAC_END（0x1B）を送る。
            if (_sourceEnded && dacStreamOpen && !dacEndSent && pendingDacOps.Count == 0 && pendingEvents.Count == 0 && _txQueue.IsEmpty)
            {
                byte[] endPayload = BuildDacEndPayload(dacSequence, dacPosition);
                byte[] endPacket = BuildFramedPacket(0x00, 0x1B, 0, endPayload);
                lock (_lock)
                {
                    _activeStream?.Write(endPacket, 0, endPacket.Length);
                }
                dacEndSent = true;
            }

            // 5. 実機のシリアルでは、DAC_STATUS（0x1C）を応答を待たずに定期的に要求する。応答待ちの間は間隔を広げる。
            if (dacStartSent && _playbackClock.IsRunning && _serialPort != null)
            {
                ulong elapsedMs = (ulong)_playbackClock.ElapsedMilliseconds;
                ulong statusInterval = statusRequestInFlight ? 1000UL : 300UL;
                if (elapsedMs >= lastStatusQueryMs + statusInterval)
                {
                    lastStatusQueryMs = elapsedMs;
                    statusRequestInFlight = true;
                    try
                    {
                        payloadBuffer[0] = 0x01;
                        byte[] packet = BuildFramedPacket(0x01, 0x1C, _transactionCounter++, payloadBuffer.Slice(0, 1));
                        lock (_lock)
                        {
                            _activeStream?.Write(packet, 0, packet.Length);
                        }
                    }
                    catch (Exception ex) { MarkLinkLost("status request", ex); }
                }
            }

            // 6. desiredHorizon まで送り終えていたら少し待つ。
            if (dacPosition >= desiredHorizon && !(_sourceEnded && (pendingDacOps.Count > 0 || pendingEvents.Count > 0)))
            {
                _txSignal.WaitOne(2);
            }
        }
    }

    /// <summary>届いている受信バイトを待たずに読み出し、枠ごとに HandleDeviceReply へ渡す。</summary>
    private void DrainSerialRepliesNonBlocking(
        byte[] rxScratch,
        List<byte> rxAssembler,
        ref bool dacStreamOpen,
        ref bool dacStartSent,
        ref bool dacEndSent,
        ref uint dacSequence,
        ref ulong dacPosition,
        ref ulong lastSentEventTick,
        ref ulong streamBaseSample,
        ref bool statusRequestInFlight,
        ref uint totalEventsSent,
        ref uint estimatedDeviceQueuedRecords,
        ref uint estimatedDeviceQueuedEvents,
        ref ulong anchorPlayedTick,
        ref long anchorStopwatchTicks,
        Queue<(ulong EndTick, int Count)> inFlightRecords,
        Queue<(ulong Tick, int Count)> inFlightEvents,
        Queue<NdsifWrite> pendingEvents,
        Queue<(ulong Tick, byte Kind, byte Val)> pendingDacOps,
        ref byte[] streamBuffer)
    {
        try
        {
            while (true)
            {
                int avail;
                int bytesRead = 0;
                // ポートにあるだけ読む。なければ抜ける。
                lock (_lock)
                {
                    if (_serialPort == null || !_serialPort.IsOpen) return;
                    avail = _serialPort.BytesToRead;
                    if (avail <= 0) break;
                    bytesRead = _serialPort.Read(rxScratch, 0, Math.Min(avail, rxScratch.Length));
                }

                if (bytesRead <= 0) break;

                // 区切りの0で1枠とし、解析できたら応答を処理する。長すぎる枠は捨てる。
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = rxScratch[i];
                    if (b == 0x00)
                    {
                        if (rxAssembler.Count > 0)
                        {
                            if (TryParseFrame(rxAssembler.ToArray(), out var msg))
                            {
                                HandleDeviceReply(
                                    msg,
                                    ref dacStreamOpen,
                                    ref dacStartSent,
                                    ref dacEndSent,
                                    ref dacSequence,
                                    ref dacPosition,
                                    ref lastSentEventTick,
                                    ref streamBaseSample,
                                    ref statusRequestInFlight,
                                    ref totalEventsSent,
                                    ref estimatedDeviceQueuedRecords,
                                    ref estimatedDeviceQueuedEvents,
                                    ref anchorPlayedTick,
                                    ref anchorStopwatchTicks,
                                    inFlightRecords,
                                    inFlightEvents,
                                    pendingEvents,
                                    pendingDacOps,
                                    ref streamBuffer);
                            }
                            rxAssembler.Clear();
                        }
                    }
                    else
                    {
                        rxAssembler.Add(b);
                        if (rxAssembler.Count > 512) rxAssembler.Clear();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MarkLinkLost("RX drain", ex);
        }
    }

    /// <summary>
    /// DAC_STATUS（0x1C）の応答を処理する。
    /// 再生中なら時計の基準と溜まり量の見積もりを合わせ、異常や予期しない終了ならストリームをリセットして現在位置からやり直す。
    /// </summary>
    private void HandleDeviceReply(
        NdsifMessage statusResp,
        ref bool dacStreamOpen,
        ref bool dacStartSent,
        ref bool dacEndSent,
        ref uint dacSequence,
        ref ulong dacPosition,
        ref ulong lastSentEventTick,
        ref ulong streamBaseSample,
        ref bool statusRequestInFlight,
        ref uint totalEventsSent,
        ref uint estimatedDeviceQueuedRecords,
        ref uint estimatedDeviceQueuedEvents,
        ref ulong anchorPlayedTick,
        ref long anchorStopwatchTicks,
        Queue<(ulong EndTick, int Count)> inFlightRecords,
        Queue<(ulong Tick, int Count)> inFlightEvents,
        Queue<NdsifWrite> pendingEvents,
        Queue<(ulong Tick, byte Kind, byte Val)> pendingDacOps,
        ref byte[] streamBuffer)
    {
        if (statusResp.CommandId != 0x1C || statusResp.MessageType != 0x02)
            return;

        statusRequestInFlight = false;

        // DacStream::status は72バイトを返す（Payload[0] の状態バイトと合わせて73バイト）。
        if (statusResp.Payload.Length < 73 || statusResp.Payload[0] != 0x00 || statusResp.Payload[1] != 0x01)
            return;

        // 状態は dac_stream.h の列挙で、0=Ready、1=Running、2=Ended、3=Faulted。
        byte streamState = statusResp.Payload[2];
        byte faultCode = statusResp.Payload[3];
        uint consumedSeq = BinaryPrimitives.ReadUInt32LittleEndian(statusResp.Payload.AsSpan(9, 4));
        uint consumedEvents = BinaryPrimitives.ReadUInt32LittleEndian(statusResp.Payload.AsSpan(25, 4));
        ulong playedTick = BinaryPrimitives.ReadUInt64LittleEndian(statusResp.Payload.AsSpan(57, 8));

        // Running なら、基板の再生ティックを時計の基準にして、溜まり量の見積もりを実際の値に合わせる。
        if (streamState == 1)
        {
            anchorPlayedTick = playedTick;
            anchorStopwatchTicks = _playbackClock.ElapsedTicks;

            estimatedDeviceQueuedRecords = dacSequence >= consumedSeq ? dacSequence - consumedSeq : 0;
            estimatedDeviceQueuedEvents = totalEventsSent >= consumedEvents ? totalEventsSent - consumedEvents : 0;

            while (inFlightRecords.TryPeek(out var rf) && rf.EndTick <= playedTick)
                inFlightRecords.Dequeue();
            while (inFlightEvents.TryPeek(out var ef) && ef.Tick <= playedTick)
                inFlightEvents.Dequeue();
        }
        else if (streamState == 3 || (streamState == 2 && !_sourceEnded))
        {
            // Faulted（3）か、曲の途中での Ended（2）なら、基板をリセットして現在位置から送り直し、YM2612 の音色を書き戻す。
            Console.WriteLine($"[NDSIF] DAC stream fault detected (state={streamState}, faultCode={faultCode}); recovering stream cleanly...");
            uint currentAbsSample = Volatile.Read(ref _parsedSample);

            lock (_lock)
            {
                if (_activeStream != null && _serialPort != null)
                {
                    _serialPort.DiscardInBuffer();
                    byte[] resetPacket = BuildFramedPacket(0x01, 0x00, _transactionCounter++, ReadOnlySpan<byte>.Empty);
                    _activeStream.Write(resetPacket, 0, resetPacket.Length);
                    _activeStream.Flush();
                    TryReadFrame(_activeStream, 80, out _);
                }
                _baseSampleOffset = currentAbsSample;
            }

            streamBaseSample = currentAbsSample;
            dacSequence = 0;
            dacPosition = 0;
            lastSentEventTick = 0;
            dacStreamOpen = false;
            dacStartSent = false;
            dacEndSent = false;
            totalEventsSent = 0;
            estimatedDeviceQueuedRecords = 0;
            estimatedDeviceQueuedEvents = 0;
            anchorPlayedTick = 0;
            anchorStopwatchTicks = 0;
            inFlightRecords.Clear();
            inFlightEvents.Clear();
            pendingEvents.Clear();
            pendingDacOps.Clear();

            int restorePos = 0;
            RestoreYmStateImmediate(ref streamBuffer, ref restorePos);
        }
    }

    /// <summary>
    /// Timed DAC に対応しない基板への即時送信を1回分行う。
    /// 発音時刻が来た書き込みを同じ音源ごとにまとめて送る。
    /// </summary>
    private void RunImmediateTxStep(
        Span<byte> payloadBuffer,
        Span<(byte reg, byte val)> ymBatch,
        Span<byte> psgBatch,
        ref byte[] streamBuffer)
    {
        // 送るものがないか時計が止まっていれば待つ。
        if (_txQueue.IsEmpty)
        {
            _txSignal.WaitOne(5);
            return;
        }

        if (!_playbackClock.IsRunning)
        {
            _txSignal.WaitOne(5);
            return;
        }

        if (!_txQueue.TryPeek(out var first))
            return;

        // 先頭の時刻がまだなら、残り時間に応じて待つ・譲る・空回りする。
        if (first.TargetSample > 0)
        {
            long elapsedTicks = _playbackClock.ElapsedTicks;
            double elapsedSec = (double)elapsedTicks / Stopwatch.Frequency;
            uint currentSample = _baseSampleOffset + (uint)(elapsedSec * 44100.0);

            if (currentSample < first.TargetSample)
            {
                uint diff = first.TargetSample - currentSample;
                double msUntil = diff * 1000.0 / 44100.0;
                if (msUntil > 2.5)
                {
                    int waitMs = (int)(msUntil - 1.5);
                    _txSignal.WaitOne(Math.Clamp(waitMs, 1, 10));
                    return;
                }
                else if (msUntil > 0.5)
                {
                    Thread.Yield();
                    return;
                }
                else
                {
                    Thread.SpinWait(20);
                    return;
                }
            }
        }

        // いまの時刻までの書き込みを、最大16パケットまで送る。
        int totalPacketsEncoded = 0;
        int streamBufferPos = 0;

        long nowTicks = _playbackClock.ElapsedTicks;
        uint nowSample = _baseSampleOffset + (uint)((double)nowTicks / Stopwatch.Frequency * 44100.0);

        while (_txQueue.TryPeek(out var next) && totalPacketsEncoded < 16)
        {
            if (next.TargetSample > nowSample)
            {
                break;
            }

            // YM2612 は同じポートの続きを最大60組まとめる。
            if (next.Cmd is 0x52 or 0x53)
            {
                byte currentPort = next.Port;
                byte currentCmd = next.Cmd;
                int count = 0;

                while (count < 60 && _txQueue.TryPeek(out var item) &&
                       item.Cmd == currentCmd && item.Port == currentPort &&
                       item.TargetSample <= nowSample)
                {
                    _txQueue.TryDequeue(out _);
                    _ymRegs[currentPort, item.Reg] = item.Val;
                    // 即時送信では、続けて来た DAC のサンプル値（0x2A）は最後の値にまとめ、遅れた DAC サンプルでバスがあふれないようにする。
                    if (currentPort == 0 && item.Reg == 0x2A && count > 0 && ymBatch[count - 1].reg == 0x2A)
                    {
                        ymBatch[count - 1] = (0x2A, item.Val);
                    }
                    else
                    {
                        ymBatch[count++] = (item.Reg, item.Val);
                    }
                }

                if (count == 0) break;

                // ペイロードはインスタンス0、ページ（ポート0/1）、レジスタと値の組。
                int payloadLen = 2 + count * 2;
                payloadBuffer[0] = 0;
                payloadBuffer[1] = currentPort;
                for (int i = 0; i < count; i++)
                {
                    payloadBuffer[2 + i * 2] = ymBatch[i].reg;
                    payloadBuffer[3 + i * 2] = ymBatch[i].val;
                }

                SendImmediateFrameToBuffer(0x00, currentCmd, 0, payloadBuffer.Slice(0, payloadLen), ref streamBuffer, ref streamBufferPos);
                totalPacketsEncoded++;
            }
            else if (next.Cmd is 0x50 or 0x30)
            {
                // SN76489 は同じ番号の続きを最大120バイトまとめる。ペイロードはインスタンス（0/1）、ページ0、データ。
                byte currentCmd = next.Cmd;
                byte currentInstance = next.Port;
                int count = 0;

                while (count < 120 && _txQueue.TryPeek(out var item) &&
                       item.Cmd == currentCmd && item.Port == currentInstance &&
                       item.TargetSample <= nowSample)
                {
                    _txQueue.TryDequeue(out _);
                    psgBatch[count++] = item.Val;
                }

                if (count == 0) break;

                int payloadLen = 2 + count;
                payloadBuffer[0] = currentInstance;
                payloadBuffer[1] = 0;
                for (int i = 0; i < count; i++)
                {
                    payloadBuffer[2 + i] = psgBatch[i];
                }

                SendImmediateFrameToBuffer(0x00, currentCmd, 0, payloadBuffer.Slice(0, payloadLen), ref streamBuffer, ref streamBufferPos);
                totalPacketsEncoded++;
            }
            else
            {
                _txQueue.TryDequeue(out _);
            }
        }

        FlushStreamBuffer(streamBuffer, ref streamBufferPos);
    }

}
