using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm
{
    /// <summary>
    /// VGMの再生時刻、レジスタ、鍵盤表示を追う。音声は作らず、音声の生成は再生エンジン側が受け持つ。
    /// ND6を使うときは、YM2612とSN76489の書込みをここから実機へ送る。
    /// </summary>
    internal sealed class VgmRegisterPlaybackMonitor : IDisposable
    {
        private const int SampleRate = 44100;
        public VgmFile Vgm { get; }
        public bool IsPlaying { get; private set; } = true;
        public bool IsPaused { get; set; } = false;


        // 音源チップごとの表示用の監視。
        private readonly VgmOpnaActivityMonitor _opnAudio;
        private readonly VgmSegaPcmMonitor? _segaPcm;
        private readonly VgmHuC6280Monitor? _huc6280;
        private readonly VgmExtraRegisterMonitor _additional;
        private readonly VgmRegisterMonitor[] _sccMonitors, _opllMonitors, _opl2Monitors, _oplMonitors, _opl3Monitors;
        private readonly VgmRegisterMonitor[] _fmRegisterMonitors;
        private readonly VgmEs5503Monitor[] _es5503Monitors;
        // クロック欄から、1個目と（ビット30があれば）2個目の監視を作る。
        private static VgmRegisterMonitor[] ChipMonitors(string chip, uint clock) =>
            Enumerable.Range(0, (clock & 0x3fffffff) == 0 ? 0 : (clock & 0x40000000) != 0 ? 2 : 1)
                .Select(instance => new VgmRegisterMonitor(chip, clock, instance)).ToArray();
        // 再生側と共通の命令列と、読出し位置・待ち時間・ループ回数。
        private readonly IndependentVgmProgram _program;
        private readonly byte[] _vgmData;
        private int _vgmPos, _commandIndex;
        private int _waitSamples = 0;
        private readonly VgmWaitTimer _waits = new();
        private bool _sourceEnded;
        private uint _renderedSamples = 0;
        private bool _noteCaptureDirty;
        private readonly KeyboardActivity _keyboardActivity = new();
        private int _loopCount = 0;

        // OPM・OPN・PCMの状態（プレイヤー表示用）。
        private readonly OpmChannelState[] _opmStates = new OpmChannelState[8];
        private readonly PcmChannelState[] _pcmStates = new PcmChannelState[8];
        private readonly int[] _opnFNums = new int[6];
        private readonly int[] _opnBlocks = new int[6];
        private readonly object _stateLock = new();
        private readonly VgmChannelMonitor _channelMonitor;
        private MusicChannelSnapshot[] _latestChannels = [];
        internal MusicChannelSnapshot[] LatestChannels { get { lock (_stateLock) return _latestChannels; } }
        private int _ym2612PcmPosition;
        private readonly VgmPcmBanks _pcmBanks = new();
        private readonly VgmDacStreams _dacStreams;

        // OPMのキーコードから音名への対応表。
        private static readonly string[] NoteNames = { "C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-" };
        private static readonly int[] KcToNoteMap = { 0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11 };

        private readonly bool _isNd6Active;
        /// <summary>表示用の監視を用意する。</summary>
        /// <param name="vgm">対象の曲。</param>
        /// <param name="isNd6Active">ND6でYM2612とSN76489を実機出力しているか。</param>
        /// <param name="program">再生側で解析済みの命令列。省略時はここで解析する（再生できないVGMは例外）。</param>
        internal VgmRegisterPlaybackMonitor(VgmFile vgm, bool isNd6Active = false, IndependentVgmProgram? program = null)
        {
            _program = program ?? IndependentVgmProgram.Parse(vgm);
            _channelMonitor = new VgmChannelMonitor(vgm, true);
            // DACストリームの書込みは、YM2612のDACとOKIM6258だけを表示に反映する。ND6では設定し直しても止めない。
            _dacStreams = new VgmDacStreams(_pcmBanks,(chip,port,register,value)=>
            {
                if (chip==2 && port==0 && register==0x2a) WriteYm2612Dac(value);
                else if (chip==0x17 && port==0 && register<=2)
                {
                    HandleOkiWrite(register,value); _channelMonitor.WritePcm("OKIM6258");
                }
            }, strictBounds: false, stopOnSetup: !isNd6Active);
            _isNd6Active = isNd6Active;
            Vgm = vgm;
            _vgmData = _program.Data;
            // OPL系・SCC・ES5503の監視と、その他の音源チップ用の監視を作る。ES5503の出力数はヘッダーの0xD4から読む。
            _sccMonitors = ChipMonitors("K051649", vgm.K051649Clock);
            _opllMonitors = ChipMonitors("YM2413", vgm.Ym2413Clock);
            _opl2Monitors = ChipMonitors("YM3812", vgm.Ym3812Clock);
            _oplMonitors = ChipMonitors("YM3526", vgm.Ym3526Clock);
            _opl3Monitors = ChipMonitors("YMF262", vgm.Ymf262Clock);
            _fmRegisterMonitors = [.. _opllMonitors, .. _opl2Monitors, .. _oplMonitors, .. _opl3Monitors];
            _es5503Monitors = Enumerable.Range(0, (vgm.Es5503Clock & 0x3fffffff) == 0 ? 0 :
                (vgm.Es5503Clock & 0x40000000) != 0 ? 2 : 1)
                .Select(instance => new VgmEs5503Monitor(vgm.Es5503Clock, instance,
                    vgm.RawData.Length > 0xd4 ? vgm.RawData[0xd4] : (byte)2)).ToArray();
            _opnAudio = new VgmOpnaActivityMonitor(vgm, SampleRate);
            _additional = new VgmExtraRegisterMonitor(vgm, SampleRate);
            if (vgm.SegaPcmClock != 0) _segaPcm = new VgmSegaPcmMonitor(vgm, SampleRate);
            if (vgm.HuC6280Clock != 0) _huc6280 = new VgmHuC6280Monitor(vgm, SampleRate);

            // OPMとPCMの8チャンネル分の状態を用意し、最初の表示を作る。
            for (int i = 0; i < 8; i++)
            {
                _opmStates[i] = new OpmChannelState { ChannelIndex = i };
                _pcmStates[i] = new PcmChannelState { ChannelIndex = i };
            }
            _latestChannels = CaptureMusicChannels(new VgmVisualSnapshot { IsPlaying = IsPlaying, CurrentTime = TimeSpan.Zero });
        }

        /// <summary>指定サンプル数だけ再生位置を進め、その間の命令を処理して表示を更新する。</summary>
        public void Advance(int samplesNeeded)
        {
            if (!IsPlaying || IsPaused) return;
            int samplesWritten = 0;

            while (samplesWritten < samplesNeeded && IsPlaying)
            {
                // 待ち時間が尽きたら次の命令を処理し、音高に関わる書込みがあれば表示用のチャンネル情報を作り直す。
                if (_waitSamples <= 0)
                {
                    ParseNextVgmCommands();
                    if (_noteCaptureDirty)
                    {
                        lock (_stateLock)
                        {
                            var state = new VgmVisualSnapshot { IsPlaying = IsPlaying, CurrentTime = TimeSpan.FromSeconds(_renderedSamples / (double)SampleRate) };
                            for (int index = 0; index < _pcmStates.Length; index++) state.PcmChannels[index] = _pcmStates[index];
                            _latestChannels = CaptureMusicChannels(state);
                            _keyboardActivity.Record(_latestChannels);
                        }
                        _noteCaptureDirty = false;
                    }
                    if (_sourceEnded) continue;
                    if (!IsPlaying) break;
                }

                // 待ち時間、DACストリーム、残りのサンプル数のうち一番近いところまで進める。
                _dacStreams.EmitDue();
                int chunk = Math.Min(_waitSamples, samplesNeeded - samplesWritten);
                chunk = Math.Min(chunk, _dacStreams.FramesUntilWrite());
                if (chunk <= 0) chunk = 1;

                // サンプルの終わりを示す表示だけを進め、音声は二重に作らない。
                _opnAudio.Advance(chunk);
                _additional.Advance(chunk);
                foreach (var monitor in _es5503Monitors) monitor.Advance(chunk);

                _waitSamples -= chunk;
                _dacStreams.Advance(chunk);
                samplesWritten += chunk;
                _renderedSamples += (uint)chunk;
            }

            // 末尾がちょうど待ち時間の境目なら、次の命令も処理しておく。
            if (!_sourceEnded && _waitSamples <= 0)
            {
                ParseNextVgmCommands();
                _dacStreams.EmitDue();
            }

            // ND6には再生済みのサンプル位置を知らせる。
            if (_isNd6Active)
            {
                MirrorFlux.Services.Hardware.NdsifService.Instance.AdvanceSample(_renderedSamples);
            }

            UpdateLevelDecay();
        }


        /// <summary>3バイトの値をリトルエンディアンで読み、位置を進める。</summary>
        private static int ReadUInt24(byte[] data, ref int position)
        {
            int value = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
            position += 3;
            return value;
        }

        /// <summary>PCMバンクの内容を配列で返す。なければ空。</summary>
        private byte[] GetPcmBank(byte bank) => _pcmBanks.Get(bank)?.Bytes.ToArray() ?? [];

        /// <summary>次の待ち時間まで命令を処理する。</summary>
        private void ParseNextVgmCommands()
        {
            // 命令の境目、長さ、ループ位置は、音声再生と同じ解析結果を使う。_vgmPosは現在の命令の最初の引数を指す。
            while (_waitSamples <= 0 && !_sourceEnded)
            {
                if (_commandIndex >= _program.Commands.Count) { MarkSourceEnded(); return; }
                var command = _program.Commands[_commandIndex++];
                byte cmd = command.Code;
                _vgmPos = command.Offset + 1;
                // レジスタ書込みは表示の作り直しを要求する。ただしDACへの書込み、サンプル転送、FMの音色設定は音高を変えないので除く。
                if (cmd is 0x30 or 0x3f or >= 0x4f and <= 0x5f or 0xa0 or 0xa1 or >= 0xa5 and <= 0xa7 or 0xaa or 0xab or 0xae or 0xaf or >= 0xb0 and <= 0xbf or >= 0xc0 and <= 0xdf)
                {
                    bool skipNoteDirty = false;
                    if ((cmd is 0x52 or 0x53) && _vgmPos < _vgmData.Length)
                    {
                        byte ymReg = _vgmData[_vgmPos];
                        if ((cmd == 0x52 && ymReg is 0x2a or 0x2b) ||
                            (ymReg >= 0x30 && ymReg <= 0x3f) ||
                            (ymReg >= 0x50 && ymReg <= 0x9e))
                        {
                            skipNoteDirty = true;
                        }
                    }
                    else if (cmd == 0x57 && _vgmPos < _vgmData.Length && _vgmData[_vgmPos] == 8)
                    {
                        skipNoteDirty = true;
                    }

                    if (!skipNoteDirty) _noteCaptureDirty = true;
                }

                switch (cmd)
                {
                    // 0x30: 2個目のSN76489への書込み。
                    case 0x30:
                        if (_vgmPos < _vgmData.Length)
                        {
                            byte val = _vgmData[_vgmPos++];
                            if (_isNd6Active) MirrorFlux.Services.Hardware.NdsifService.Instance.WriteSn76489(1, val, _renderedSamples);
                            _channelMonitor.WritePsg(val, 1);
                        }
                        break;

                    // 0x3F: 2個目のSN76489のGame Gearステレオ設定。
                    case 0x3F:
                        if (_vgmPos < _vgmData.Length)
                        {
                            byte val = _vgmData[_vgmPos++];
                            _channelMonitor.WritePsgStereo(val, 1);
                        }
                        break;

                    // 0x4F: Game Gearステレオ設定。
                    case 0x4F:
                        if (_vgmPos < _vgmData.Length)
                        {
                            byte val = _vgmData[_vgmPos++];
                            _channelMonitor.WritePsgStereo(val, 0);
                        }
                        break;

                    // 0x50: SN76489への書込み。
                    case 0x50:
                        if (_vgmPos < _vgmData.Length)
                        {
                            byte val = _vgmData[_vgmPos++];
                            if (_isNd6Active) MirrorFlux.Services.Hardware.NdsifService.Instance.WriteSn76489(0, val, _renderedSamples);
                            _channelMonitor.WritePsg(val, 0);
                        }
                        break;

                    // 0x52: YM2612のポート0への書込み。0x2AはDAC。
                    case 0x52:
                        if (_vgmPos + 1 < _vgmData.Length)
                        {
                            byte reg = _vgmData[_vgmPos++];
                            byte val = _vgmData[_vgmPos++];
                            if (reg == 0x2a) WriteYm2612Dac(val);
                            else
                            {
                                if (_isNd6Active) MirrorFlux.Services.Hardware.NdsifService.Instance.WriteYm2612(0, reg, val, _renderedSamples);
                                else _opnAudio.Write(0x52, 0, reg, val);
                            }
                            HandleOpnWrite(0, reg, val);
                            _channelMonitor.WriteFm("YM2612", 0, reg, val);
                        }
                        break;

                    // 0x53: YM2612のポート1への書込み。
                    case 0x53:
                        if (_vgmPos + 1 < _vgmData.Length)
                        {
                            byte reg = _vgmData[_vgmPos++];
                            byte val = _vgmData[_vgmPos++];
                            if (_isNd6Active) MirrorFlux.Services.Hardware.NdsifService.Instance.WriteYm2612(1, reg, val, _renderedSamples);
                            else _opnAudio.Write(0x53, 1, reg, val);
                            HandleOpnWrite(1, reg, val);
                            _channelMonitor.WriteFm("YM2612", 1, reg, val);
                        }
                        break;

                    // 0x54: YM2151への書込み。
                    case 0x54:
                        if (_vgmPos + 1 < _vgmData.Length)
                        {
                            byte reg = _vgmData[_vgmPos++];
                            byte val = _vgmData[_vgmPos++];
                            HandleOpmWrite(reg, val);
                            _channelMonitor.WriteFm("YM2151", 0, reg, val);
                        }
                        break;

                    // 0x55・0xA5: YM2203への書込み。0xA5はヘッダーのクロックのビット30で宣言された2個目。
                    case 0x55:
                    case 0xa5:
                        if (_vgmPos + 1 < _vgmData.Length)
                        {
                            byte reg = _vgmData[_vgmPos++];
                            byte val = _vgmData[_vgmPos++];
                            _opnAudio.Write(cmd, 0, reg, val);
                            if (cmd == 0x55) HandleOpnWrite(0, reg, val);
                            _channelMonitor.WriteFm("YM2203", 0, reg, val, cmd == 0xa5 ? 1 : 0);
                        }
                        break;

                    // 0x56・0x57・0xA6・0xA7: YM2608のポート0・1、1個目・2個目への書込み。
                    case 0x56: case 0x57: case 0xa6: case 0xa7:
                        if (_vgmPos + 1 < _vgmData.Length)
                        {
                            byte reg = _vgmData[_vgmPos++], val = _vgmData[_vgmPos++];
                            int port = cmd & 1, instance = cmd >= 0xa6 ? 1 : 0;
                            _opnAudio.Write(cmd, port, reg, val);
                            if (instance == 0) HandleOpnWrite(port, reg, val);
                            _channelMonitor.WriteFm("YM2608", port, reg, val, instance);
                        }
                        break;

                    // レジスタから表示状態を作るその他の音源チップ。
                    // 0x51・0xA1: YM2413、0x58・0x59: YM2610のポート0・1、0x5A・0xAA: YM3812、0x5B・0xAB: YM3526、0x5C: Y8950、0x5D: YMZ280B、
                    // 0x5E・0x5F・0xAE・0xAF: YMF262、0xA0: AY-3-8910、0xB2: 32XのPWM、0xB3: Game Boy、0xB4: NES APU、0xB8: OKIM6295、
                    // 0xBA: K053260、0xB0: RF5C68、0xB1: RF5C164、0xB5: MultiPCM、0xBF: Irem GA20。
                    case 0x51: case 0xa1:
                    case 0x58: case 0x59:
                    case 0x5a: case 0xaa:
                    case 0x5b: case 0xab:
                    case 0x5c:
                    case 0x5d:
                    case 0x5e: case 0x5f: case 0xae: case 0xaf:
                    case 0xa0:
                    case 0xb2:
                    case 0xb3:
                    case 0xb4:
                    case 0xb8:
                    case 0xba:
                    case 0xb0:
                    case 0xb1:
                    case 0xb5:
                    case 0xbf:
                        if (_vgmPos + 1 < _vgmData.Length)
                        {
                            byte reg = _vgmData[_vgmPos++];
                            byte val = _vgmData[_vgmPos++];
                            var monitors = cmd switch {
                                0x51 or 0xa1 => _opllMonitors, 0x5a or 0xaa => _opl2Monitors,
                                0x5b or 0xab => _oplMonitors, 0x5e or 0x5f or 0xae or 0xaf => _opl3Monitors, _ => null };
                            monitors?.ElementAtOrDefault(cmd >= 0xa1 ? 1 : 0)
                                ?.WriteOpl((cmd is 0x5f or 0xaf ? 0x100 : 0) + reg, val);
                            _additional.Write(cmd, reg, val);
                            if (cmd is 0x58 or 0x59) _channelMonitor.WriteFm("YM2610", cmd - 0x58, reg, val);
                        }
                        break;

                    // 0xD0: YMF278B、0xD1: YMF271。ポート・レジスタ・値の順。
                    case 0xd0:
                    case 0xd1:
                        if (_vgmPos + 2 < _vgmData.Length)
                        {
                            int port = _vgmData[_vgmPos++] & 0x7f;
                            int address = _vgmData[_vgmPos++];
                            int value = _vgmData[_vgmPos++];
                            _additional.Write(cmd == 0xd0 ? "YMF278B" : "YMF271", port, address, value);
                        }
                        break;

                    // 0xC1・0xC2: RF5C68・RF5C164のRAMへの書込み。
                    case 0xc1:
                    case 0xc2:
                        if (_vgmPos + 2 < _vgmData.Length)
                        {
                            int address = _vgmData[_vgmPos++] | (_vgmData[_vgmPos++] << 8);
                            byte value = _vgmData[_vgmPos++];
                            _additional.WriteRam(cmd == 0xc1 ? "RF5C68" : "RF5C164", address, value);
                        }
                        break;

                    // 0xC3: MultiPCMのバンク指定。
                    case 0xc3:
                        if (_vgmPos + 2 < _vgmData.Length)
                        {
                            byte bank = _vgmData[_vgmPos++];
                            ushort value = (ushort)(_vgmData[_vgmPos++] | (_vgmData[_vgmPos++] << 8));
                            _additional.WriteMultiPcmBank(bank, value);
                        }
                        break;

                    // 0xC4: QSound。16ビット値の後にレジスタ番号。
                    case 0xc4:
                        if (_vgmPos + 2 < _vgmData.Length)
                        {
                            int value = (_vgmData[_vgmPos++] << 8) | _vgmData[_vgmPos++];
                            int address = _vgmData[_vgmPos++];
                            _additional.Write("QSound", 0, address, value);
                        }
                        break;

                    // 0xD2: K051649（SCC）のポート・レジスタ・値、0xD3: K054539の16ビットレジスタ・値、
                    // 0xD4: C140のバンク・レジスタ・値、0xD5: ES5503の番号付きレジスタ・値。
                    case 0xd2:
                    case 0xd3:
                    case 0xd4:
                    case 0xd5:
                        if (_vgmPos + 2 < _vgmData.Length)
                        {
                            int first = _vgmData[_vgmPos++];
                            int second = _vgmData[_vgmPos++];
                            int value = _vgmData[_vgmPos++];
                            if (cmd == 0xd2)
                            {
                                _sccMonitors.ElementAtOrDefault(first >> 7)?.WriteScc(first & 0x7f, second, (byte)value);
                                if (first < 0x80) _additional.Write("K051649", first, second, value);
                            }
                            else if (cmd == 0xd3) _additional.Write("K054539", 0, (first & 0x7f) << 8 | second, value);
                            else if (cmd == 0xd4) _additional.Write("C140", 0, (first & 0x7f) << 8 | second, value);
                            else _es5503Monitors.ElementAtOrDefault(first >> 7)?.Write(second, (byte)value);
                        }
                        break;

                    // 0xE1: C352。16ビットのレジスタと16ビットの値。
                    case 0xe1:
                        if (_vgmPos + 3 < _vgmData.Length)
                        {
                            int address = (_vgmData[_vgmPos++] << 8) | _vgmData[_vgmPos++];
                            int value = (_vgmData[_vgmPos++] << 8) | _vgmData[_vgmPos++];
                            _additional.Write("C352", 0, address, value);
                        }
                        break;

                    // 0xC0: SegaPCMのRAMへの書込み。アドレスはリトルエンディアン。
                    case 0xc0:
                        if (_vgmPos + 2 < _vgmData.Length)
                        {
                            int address = _vgmData[_vgmPos++] | (_vgmData[_vgmPos++] << 8);
                            byte value = _vgmData[_vgmPos++];
                            if ((address & 0x8000) == 0) _segaPcm?.Write(address, value);
                        }
                        break;

                    // 0xB7: OKIM6258への書込み。
                    case 0xB7:
                        if (_vgmPos + 1 < _vgmData.Length)
                        {
                            byte reg = _vgmData[_vgmPos++];
                            byte val = _vgmData[_vgmPos++];
                            HandleOkiWrite(reg, val);
                            _channelMonitor.WritePcm("OKIM6258");
                        }
                        break;

                    // 0xB9: HuC6280への書込み。ビット7は2個目の指定。
                    case 0xB9:
                        if (_vgmPos + 1 < _vgmData.Length)
                        {
                            byte reg = _vgmData[_vgmPos++];
                            byte val = _vgmData[_vgmPos++];
                            if ((reg & 0x80) == 0) _huc6280?.Write((byte)(reg & 0x7f), val);
                        }
                        break;

                    // 0x61〜0x63・0x7n: 待ち時間。0x64は0x62・0x63の待ち時間の上書き。
                    // 0x8nは直近のPCMブロックの次の1バイトをYM2612のDACへ書いてからnサンプル待つ。
                    case >= 0x61 and <= 0x64 or >= 0x70 and <= 0x8f:
                        if (cmd >= 0x80)
                        {
                            var pcm=_pcmBanks.Get(0)?.Data;
                            if (pcm!=null && _ym2612PcmPosition < pcm.Count)
                                WriteYm2612Dac(pcm[_ym2612PcmPosition++]);
                        }
                        _waits.TryWait(_vgmData.AsSpan(command.Offset, command.Length), out int waited);
                        _waitSamples += waited;
                        break;
                    // 0x66: 曲の終わり。ループがあればループ位置へ戻る。
                    case 0x66:
                        if (_program.LoopIndex >= 0)
                        {
                            _commandIndex = _program.LoopIndex;
                            _loopCount++;
                        }
                        else
                        {
                            MarkSourceEnded();
                            return;
                        }
                        break;

                    // 0x67: データブロック。種類に応じてSegaPCM・YM2608・ES5503・その他の監視、PCMバンクへ読み込む。
                    case 0x67:
                        if (_vgmPos + 6 <= _vgmData.Length)
                        {
                            _vgmPos++;
                            byte type = _vgmData[_vgmPos++];
                            uint size = (uint)(_vgmData[_vgmPos++] | (_vgmData[_vgmPos++] << 8) |
                                              (_vgmData[_vgmPos++] << 16) | (_vgmData[_vgmPos++] << 24));
                            uint payloadSize = size & 0x7fffffff;
                            int instance = (int)(size >> 31);
                            int dataLength = (int)Math.Min(payloadSize, (uint)Math.Max(0, _vgmData.Length - _vgmPos));
                            if (type == 0x80 && instance == 0) _segaPcm?.LoadRom(_vgmData.AsSpan(_vgmPos, dataLength));
                            if (type == 0x81)
                                _opnAudio.LoadOpnaMemory(instance, _vgmData.AsSpan(_vgmPos, dataLength));
                            if (type == 0xe1 && dataLength >= 4)
                            {
                                int address = _vgmData[_vgmPos] | _vgmData[_vgmPos + 1] << 8 |
                                    _vgmData[_vgmPos + 2] << 16 | _vgmData[_vgmPos + 3] << 24;
                                _es5503Monitors.ElementAtOrDefault(instance)?.LoadRam(address,
                                    _vgmData.AsSpan(_vgmPos + 4, dataLength - 4));
                            }
                            if (instance == 0) _additional.LoadRom(type, _vgmData.AsSpan(_vgmPos, dataLength));
                            if (type<=0x7f && instance==0)
                            {
                                try { _pcmBanks.Load(type,_vgmData.AsSpan(_vgmPos,dataLength),_vgmPos); }
                                catch (Exception ex) when (ex is System.IO.InvalidDataException or NotSupportedException)
                                { System.Diagnostics.Debug.WriteLine("PCM telemetry: "+ex.Message); }
                            }
                            _vgmPos += dataLength;
                        }
                        break;

                    // 0x68: PCMバンクから音源チップのRAMへの転送。
                    case 0x68:
                        if (_vgmPos + 11 <= _vgmData.Length)
                        {
                            _vgmPos++;
                            byte chipType = _vgmData[_vgmPos++];
                            int readOffset = ReadUInt24(_vgmData, ref _vgmPos);
                            int writeOffset = ReadUInt24(_vgmData, ref _vgmPos);
                            int size = ReadUInt24(_vgmData, ref _vgmPos);
                            if (size == 0) size = 0x1000000;
                            byte[] bank = GetPcmBank((byte)(chipType & 0x7f));
                            if (readOffset >= 0 && readOffset < bank.Length)
                            {
                                int count = Math.Min(size, bank.Length - readOffset);
                                _additional.LoadRam(chipType, writeOffset, bank.AsSpan(readOffset, count));
                            }
                        }
                        break;

                    // 0x90〜0x95: DACストリーム。音声再生と同じサンプルクロックとストリーム状態を使う。
                    case >= 0x90 and <= 0x95:
                        try { _dacStreams.Command(_vgmData.AsSpan(command.Offset, command.Length)); }
                        catch (NotSupportedException ex) { System.Diagnostics.Debug.WriteLine("DAC telemetry: "+ex.Message); }
                        break;

                    // 0xE0: YM2612のDAC用データ位置を移す。
                    case 0xE0:
                        _ym2612PcmPosition = (int)IndependentVgmProgram.U32(_vgmData, _vgmPos);
                        _ym2612PcmPosition = Math.Clamp(_ym2612PcmPosition, 0, (_pcmBanks.Get(0)?.Data.Count ?? 0));
                        break;

                    // それ以外の命令は表示しない。長さは解析済みの命令列から分かる。
                    default:
                        break;
                }
            }
        }

        /// <summary>曲の終わりに達したことを記録し、ND6にも知らせる。</summary>
        private void MarkSourceEnded()
        {
            if (_sourceEnded) return;
            _sourceEnded = true;
            IsPlaying = false;
            if (_isNd6Active)
            {
                MirrorFlux.Services.Hardware.NdsifService.Instance.NotifySourceEnded(_renderedSamples);
            }
        }

        /// <summary>YM2151への書込みからOPMチャンネルの表示状態を更新する。</summary>
        private void HandleOpmWrite(byte reg, byte val)
        {
            lock (_stateLock)
            {
                // 0x08: キーオン・キーオフ。ビット6〜3がスロット（M1・C1・M2・C2）、ビット2〜0がチャンネル。キーオン時はレベルを跳ね上げる。
                if (reg == 0x08)
                {
                    int ch = val & 0x07;
                    int slotMask = (val >> 3) & 0x0F;
                    var state = _opmStates[ch];
                    state.SlotMask = slotMask;
                    state.IsKeyOn = slotMask != 0;
                    state.DataString = $"08-{val:X2}";

                    if (state.IsKeyOn)
                    {
                        state.Level = Math.Max(state.Level, state.TargetLevel > 0 ? state.TargetLevel : 0.85);
                    }
                }
                // 0x20〜0x27: パン・フィードバック・アルゴリズム。
                else if (reg >= 0x20 && reg <= 0x27)
                {
                    int ch = reg - 0x20;
                    var state = _opmStates[ch];
                    state.Pan = (val >> 6) & 0x03;
                    state.Feedback = (val >> 3) & 0x07;
                    state.Algorithm = val & 0x07;
                }
                // 0x28〜0x2F: キーコード。ビット6〜4がオクターブ、ビット3〜0が音名のコード。
                else if (reg >= 0x28 && reg <= 0x2F)
                {
                    int ch = reg - 0x28;
                    var state = _opmStates[ch];
                    state.KeyCode = val;
                    int oct = (val >> 4) & 0x07;
                    int noteCode = val & 0x0F;
                    state.Octave = oct;

                    int noteIdx = (noteCode >= 0 && noteCode < KcToNoteMap.Length) ? KcToNoteMap[noteCode] : 0;
                    state.NoteNumber = (oct + 1) * 12 + noteIdx;
                    state.NoteName = $"{NoteNames[noteIdx]}{oct + 1}";
                }
                // 0x30〜0x37: キーフラクション。
                else if (reg >= 0x30 && reg <= 0x37)
                {
                    int ch = reg - 0x30;
                    _opmStates[ch].KeyFraction = val;
                }
                // 0x60〜0x7F: 総レベル（4スロット×8チャンネル）。0が最大、127が最小。
                else if (reg >= 0x60 && reg <= 0x7F)
                {
                    int ch = (reg - 0x60) & 0x07;
                    int slot = (reg - 0x60) >> 3;
                    int tl = val & 0x7F;
                    double vol = Math.Max(0.0, 1.0 - (tl / 96.0));
                    var state = _opmStates[ch];
                    state.TargetLevel = Math.Max(state.TargetLevel, vol);
                    state.Volume = (int)(vol * 127);
                }
            }
        }

        /// <summary>OPN系への書込みからFMチャンネルの表示状態を更新する。</summary>
        private void HandleOpnWrite(int port, byte reg, byte val)
        {
            lock (_stateLock)
            {
                // 0x28: キーオン・キーオフ（ポート0のみ）。チャンネル番号3と7は無効で、それ以外を0〜5に直す。
                if (port == 0 && reg == 0x28)
                {
                    int rawCh = val & 0x07;
                    if (rawCh != 3 && rawCh != 7)
                    {
                        int ch = (val & 0x04) != 0 ? (rawCh - 4 + 3) : rawCh;
                        if (ch >= 0 && ch < 6)
                        {
                            int slotMask = (val >> 4) & 0x0F;
                            var state = _opmStates[ch];
                            state.SlotMask = slotMask;
                            state.IsKeyOn = slotMask != 0;
                            state.DataString = $"28-{val:X2}";
                            if (state.IsKeyOn)
                            {
                                state.Level = Math.Max(state.Level, state.TargetLevel > 0 ? state.TargetLevel : 0.85);
                            }
                        }
                    }
                }
                // 0xA0〜0xA2・0xA4〜0xA6: F-Numberとブロック。A4〜A6はブロックと上位ビット、A0〜A2は下位ビット。
                else if (reg >= 0xA0 && reg <= 0xA6)
                {
                    int subCh = reg & 0x03;
                    if (subCh < 3)
                    {
                        int ch = port * 3 + subCh;
                        if ((reg & 0x04) != 0)
                        {
                            int block = (val >> 3) & 0x07;
                            int fnumHigh = val & 0x07;
                            _opnBlocks[ch] = block;
                            _opnFNums[ch] = (_opnFNums[ch] & 0xFF) | (fnumHigh << 8);
                        }
                        else
                        {
                            _opnFNums[ch] = (_opnFNums[ch] & 0x700) | val;
                        }

                        // F-Numberから音名（C、C#、D…）を近似で求める。
                        int f = _opnFNums[ch];
                        int noteIdx = 0;
                        int[] fTable = { 644, 682, 723, 766, 811, 860, 911, 965, 1022, 1083, 1148, 1216 };
                        int minDiff = int.MaxValue;
                        for (int n = 0; n < fTable.Length; n++)
                        {
                            int diff = Math.Abs(f - fTable[n]);
                            if (diff < minDiff) { minDiff = diff; noteIdx = n; }
                        }
                        int oct = _opnBlocks[ch];
                        var state = _opmStates[ch];
                        state.Octave = oct;
                        state.NoteNumber = (oct + 1) * 12 + noteIdx;
                        state.NoteName = $"{NoteNames[noteIdx]}{oct + 1}";
                        state.KeyCode = (byte)((oct << 4) | noteIdx);
                    }
                }
                // 0x40〜0x4F: 総レベル。
                else if (reg >= 0x40 && reg <= 0x4F)
                {
                    int subCh = reg & 0x03;
                    if (subCh < 3)
                    {
                        int ch = port * 3 + subCh;
                        int tl = val & 0x7F;
                        double vol = Math.Max(0.0, 1.0 - (tl / 96.0));
                        var state = _opmStates[ch];
                        state.TargetLevel = Math.Max(state.TargetLevel, vol);
                        state.Volume = (int)(vol * 127);
                    }
                }
                // 0xB0〜0xB2: アルゴリズムとフィードバック。
                else if (reg >= 0xB0 && reg <= 0xB2)
                {
                    int ch = port * 3 + (reg - 0xB0);
                    var state = _opmStates[ch];
                    state.Algorithm = val & 0x07;
                    state.Feedback = (val >> 3) & 0x07;
                }
                // 0xB4〜0xB6: パン。
                else if (reg >= 0xB4 && reg <= 0xB6)
                {
                    int ch = port * 3 + (reg - 0xB4);
                    _opmStates[ch].Pan = (val >> 6) & 0x03;
                }
            }
        }

        /// <summary>OKIM6258への書込みからPCMチャンネルの表示状態を更新する。</summary>
        private void HandleOkiWrite(byte reg, byte val)
        {
            lock (_stateLock)
            {
                var pcm = _pcmStates[0];
                switch (reg)
                {
                    // 0: ビット0で停止、ビット1で再生。
                    case 0:
                        if ((val & 1) != 0)
                        {
                            pcm.IsActive = false;
                            pcm.Level = 0;
                            pcm.TargetLevel = 0;
                        }
                        else if ((val & 2) != 0)
                        {
                            pcm.IsActive = true;
                        }
                        break;
                    // 1: ADPCMデータ。値の大きさから活動量の目安を求める。実際の出力のピークは再生側のReadで測る。
                    case 1:
                        if (pcm.IsActive)
                        {
                            double activity = Math.Max((val >> 4) & 7, val & 7) / 7.0;
                            pcm.TargetLevel = Math.Max(.08, activity);
                            pcm.Level = Math.Max(pcm.Level, pcm.TargetLevel);
                        }
                        break;
                    // 2: ビットが立っている側をOKIM6258のコアが消音する。
                    case 2:
                        pcm.Pan = ((val & 2) == 0 ? 2 : 0) | ((val & 1) == 0 ? 1 : 0);
                        break;
                }
            }
        }

        /// <summary>YM2612のDACへの書込みを実機または表示用監視へ送り、DACのレベル表示を更新する。</summary>
        private void WriteYm2612Dac(byte value)
        {
            if (_isNd6Active) MirrorFlux.Services.Hardware.NdsifService.Instance.WriteYm2612(0, 0x2a, value, _renderedSamples);
            else _opnAudio.Write(0x52, 0, 0x2a, value);
            _channelMonitor.WriteFm("YM2612", 0, 0x2a, value);
            lock (_stateLock)
            {
                var pcm = _pcmStates[1];
                pcm.IsActive = true;
                pcm.TargetLevel = Math.Abs(value - 128) / 127.0;
                pcm.Level = Math.Max(pcm.Level, pcm.TargetLevel);
                pcm.Pan = 3;
            }
        }

        /// <summary>レベルメーターをなめらかに減衰させる。</summary>
        private void UpdateLevelDecay()
        {
            lock (_stateLock)
            {
                for (int i = 0; i < 8; i++)
                {
                    var opm = _opmStates[i];
                    if (!opm.IsKeyOn)
                    {
                        opm.Level *= 0.88;
                        if (opm.Level < 0.01) opm.Level = 0;
                    }
                    else
                    {
                        opm.Level = Math.Max(opm.Level * 0.96, opm.TargetLevel);
                    }

                    var pcm = _pcmStates[i];
                    pcm.Level *= 0.85;
                    if (pcm.Level < 0.01) { pcm.Level = 0; pcm.IsActive = false; }
                }
            }
        }

        /// <summary>全音源チップの監視から表示用のチャンネル情報を集める。ND6で鳴らしている音源チップには実機出力の印を付ける。</summary>
        private MusicChannelSnapshot[] CaptureMusicChannels(VgmVisualSnapshot state)
        {
            var channels = new List<MusicChannelSnapshot>(_channelMonitor.Capture(state));
            _opnAudio.InsertOpnaSamples(channels, state.IsPlaying);
            if (_segaPcm != null) channels.AddRange(_segaPcm.Capture(state.IsPlaying));
            if (_huc6280 != null) channels.AddRange(_huc6280.Capture(state.IsPlaying));
            channels.AddRange(_additional.Capture(state.IsPlaying));
            foreach (var monitor in _fmRegisterMonitors) channels.AddRange(monitor.Capture(state.IsPlaying));
            foreach (var monitor in _es5503Monitors) channels.AddRange(monitor.Capture(state.IsPlaying));
            foreach (var monitor in _sccMonitors) channels.AddRange(monitor.Capture(state.IsPlaying));
            if (_isNd6Active)
            {
                for (int i = 0; i < channels.Count; i++)
                {
                    var ch = channels[i];
                    if (ch.ChipName.Equals("YM2612", StringComparison.OrdinalIgnoreCase) ||
                        ch.ChipName.StartsWith("SN764", StringComparison.OrdinalIgnoreCase))
                    {
                        channels[i] = ch with { IsHardwareOutput = true };
                    }
                }
            }
            return channels.ToArray();
        }
        /// <summary>プレイヤー表示用のスナップショットを作る。</summary>
        public VgmVisualSnapshot GetSnapshot()
        {
            lock (_stateLock)
            {
                var snap = new VgmVisualSnapshot
                {
                    IsPlaying = IsPlaying,
                    IsPaused = IsPaused,
                    CurrentTime = TimeSpan.FromSeconds((double)_renderedSamples / SampleRate),
                    TotalTime = Vgm.TotalDuration,
                    LoopDuration = Vgm.LoopDuration,
                    LoopCount = _loopCount,
                    Title = Vgm.TrackName,
                    GameTitle = Vgm.GameName,
                    Author = Vgm.Author,
                    SystemName = !string.IsNullOrWhiteSpace(Vgm.SystemName) ? Vgm.SystemName : "X68000 YM2151"
                };

                // OPMとPCMの状態を複製する。
                for (int i = 0; i < 8; i++)
                {
                    var s = _opmStates[i];
                    snap.OpmChannels[i] = new OpmChannelState
                    {
                        ChannelIndex = s.ChannelIndex,
                        IsKeyOn = s.IsKeyOn,
                        SlotMask = s.SlotMask,
                        NoteNumber = s.NoteNumber,
                        Octave = s.Octave,
                        KeyCode = s.KeyCode,
                        KeyFraction = s.KeyFraction,
                        NoteName = s.NoteName,
                        Level = s.Level,
                        Pan = s.Pan,
                        Algorithm = s.Algorithm,
                        Feedback = s.Feedback,
                        Volume = s.Volume,
                        DataString = s.DataString
                    };

                    var p = _pcmStates[i];
                    snap.PcmChannels[i] = new PcmChannelState
                    {
                        ChannelIndex = p.ChannelIndex,
                        IsActive = p.IsActive,
                        Level = p.Level,
                        Pan = p.Pan
                    };
                }

                // チャンネル情報とレジスタ一覧を集める。
                _latestChannels = CaptureMusicChannels(snap);
                snap.MusicChannels = _keyboardActivity.Apply(_latestChannels);
                snap.RegisterBanks = _channelMonitor.CaptureRegisters();
                if (_huc6280 != null) snap.RegisterBanks = [.. snap.RegisterBanks, _huc6280.CaptureRegisters()];
                snap.RegisterBanks = [.. snap.RegisterBanks, .. _additional.CaptureRegisters(),
                    .. _fmRegisterMonitors.SelectMany(m => m.Registers()), .. _sccMonitors.SelectMany(m => m.Registers()),
                    .. _es5503Monitors.Select(m => m.Registers())];
                return snap;
            }
        }

        /// <summary>表示を止め、ND6を使っていれば初期化する。</summary>
        public void Dispose()
        {
            IsPlaying = false;
            if (_isNd6Active) MirrorFlux.Services.Hardware.NdsifService.Instance.Reset();
        }

    }
}
