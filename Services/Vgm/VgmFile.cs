using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MirrorFlux.Services.Vgm
{
    /// <summary>VGM・VGZ・S98ファイルを読み込み、ヘッダーの音源チップ・クロック・長さ・ループ位置とGD3タグを保持する。</summary>
    public sealed class VgmFile
    {
        public string FormatName { get; private set; } = "VGM";
        public uint Version { get; private set; }
        public int? S98Version { get; private set; }
        public string VersionString => Version > 0 ? $"{(Version >> 8) & 0xff:X}.{(Version & 0xff):X2}" : "";
        /// <summary>画面に出すファイル形式と版の表示。</summary>
        public string FileTypeDisplay => FormatName == "S98"
            ? (S98Version.HasValue ? $"S98 Ver.{S98Version}" : "S98")
            : (Version > 0 ? $"VGM Ver.{VersionString}" : "VGM");

        /// <summary>ヘッダーでクロックが設定されている音源チップの名前を並べた表示。</summary>
        public string SoundSourceDisplay
        {
            get
            {
                var chips = new List<string>();
                if (Ym2612Clock != 0) chips.Add("YM2612");
                if (Ym2608Clock != 0) chips.Add("YM2608");
                if (Ym2151Clock != 0) chips.Add("YM2151");
                if (Ym2203Clock != 0) chips.Add("YM2203");
                if (Ym2610Clock != 0) chips.Add("YM2610");
                if (Ym2413Clock != 0) chips.Add(IsVrc7 ? "VRC7" : "YM2413");
                if (Ymf262Clock != 0) chips.Add("YMF262");
                if (Ym3812Clock != 0) chips.Add("YM3812");
                if (Ym3526Clock != 0) chips.Add("YM3526");
                if (Y8950Clock != 0) chips.Add("Y8950");
                if (Sn76489Clock != 0) chips.Add(HasSecondSn76489 ? "SN76489 x2" : "SN76489");
                if (Ay8910Clock != 0) chips.Add(HasSecondAy8910 ? "AY-3-8910 x2" : "AY-3-8910");
                if (GameBoyClock != 0) chips.Add("Game Boy DMG");
                if (NesApuClock != 0) chips.Add("NES APU");
                if (C140Clock != 0) chips.Add("Namco C140");
                if (C352Clock != 0) chips.Add("Namco C352");
                if (K054539Clock != 0) chips.Add("K054539");
                if (K053260Clock != 0) chips.Add("K053260");
                if (K051649Clock != 0) chips.Add("K051649");
                if (Es550xClock != 0) chips.Add((Es550xClock & 0x80000000) != 0 ? "ES5506" : "ES5505");
                if (Es5503Clock != 0) chips.Add("ES5503");
                if (QSoundClock != 0) chips.Add("QSound");
                if (MultiPcmClock != 0) chips.Add("MultiPCM");
                if (SegaPcmClock != 0) chips.Add("SegaPCM");
                if (OkiM6295Clock != 0) chips.Add("OKIM6295");
                if (OkiM6258Clock != 0) chips.Add("OKIM6258");
                if (PwmClock != 0) chips.Add("PWM");
                if (Ymf278BClock != 0) chips.Add("YMF278B");
                if (Ymf271Clock != 0) chips.Add("YMF271");
                if (Ymz280BClock != 0) chips.Add("YMZ280B");
                if (Rf5C68Clock != 0) chips.Add("RF5C68");
                if (Rf5C164Clock != 0) chips.Add("RF5C164");
                if (IremGa20Clock != 0) chips.Add("Irem GA20");
                if (HuC6280Clock != 0) chips.Add("HuC6280");
                return chips.Count > 0 ? string.Join(" + ", chips) : (PrimaryChipName ?? "—");
            }
        }
        public uint TotalSamples { get; private set; }
        public uint LoopSamples { get; private set; }
        public int LoopOffset { get; private set; }
        public int DataOffset { get; private set; }

        // 各音源チップのクロック。ビット30は2個目あり、ビット31は派生チップを示す。
        public uint Sn76489Clock { get; private set; }
        public bool HasSecondSn76489 => (Sn76489Clock & 0x40000000) != 0;
        public uint SecondSn76489Clock { get; private set; }
        public uint Ym2413Clock { get; private set; }
        public bool IsVrc7 => (Ym2413Clock & 0x80000000) != 0;
        public uint Ym2612Clock { get; private set; }
        public uint Ym2151Clock { get; private set; }
        public uint SegaPcmClock { get; private set; }
        public uint SegaPcmInterface { get; private set; }
        public uint Rf5C68Clock { get; private set; }
        public uint Ym2203Clock { get; private set; }
        public bool HasSecondYm2203 => (Ym2203Clock & 0x40000000) != 0;
        public uint SecondYm2203Clock { get; private set; }
        public uint Ym2608Clock { get; private set; }
        public uint Ym2610Clock { get; private set; }
        public uint Ym3812Clock { get; private set; }
        public uint Ym3526Clock { get; private set; }
        public uint Y8950Clock { get; private set; }
        public uint Ymf262Clock { get; private set; }
        public uint Ymf278BClock { get; private set; }
        public uint Ymf271Clock { get; private set; }
        public uint Ymz280BClock { get; private set; }
        public uint Rf5C164Clock { get; private set; }
        public uint Ay8910Clock { get; private set; }
        public bool HasSecondAy8910 => (Ay8910Clock & 0x40000000) != 0;
        public uint SecondAy8910Clock { get; private set; }
        public uint GameBoyClock { get; private set; }
        public uint NesApuClock { get; private set; }
        public uint MultiPcmClock { get; private set; }
        public uint PwmClock { get; private set; }
        public uint OkiM6258Clock { get; private set; }
        public uint OkiM6295Clock { get; private set; }
        public uint Es550xClock { get; private set; }
        public uint Es5503Clock { get; private set; }
        public uint K051649Clock { get; private set; }
        public uint K054539Clock { get; private set; }
        public uint C140Clock { get; private set; }
        public byte C140Type { get; private set; }
        public uint K053260Clock { get; private set; }
        public uint C352Clock { get; private set; }
        public byte C352ClockDivider { get; private set; }
        public uint QSoundClock { get; private set; }
        public uint IremGa20Clock { get; private set; }
        public byte OkiM6258Flags { get; private set; }
        public uint HuC6280Clock { get; private set; }

        public string TrackName { get; private set; } = "";
        public string GameName { get; private set; } = "";
        public string SystemName { get; private set; } = "";
        public string Author { get; private set; } = "";
        public string Notes { get; private set; } = "";
        public string FilePath { get; private set; } = "";
        public string PrimaryChipName { get; private set; } = "FM";

        public byte[] RawData { get; private set; }

        public TimeSpan TotalDuration => TimeSpan.FromSeconds((double)TotalSamples / 44100.0);
        public TimeSpan LoopDuration => TimeSpan.FromSeconds((double)LoopSamples / 44100.0);

        /// <summary>読み込み済みのデータからインスタンスを作る。</summary>
        private VgmFile(byte[] rawData)
        {
            RawData = rawData;
        }

        /// <summary>ファイルを読み込む。読めなければnull。</summary>
        public static VgmFile? Load(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                var vgm = Load(fileBytes, Path.GetFileName(filePath));
                if (vgm != null) vgm.FilePath = filePath;
                return vgm;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>展開後のVGMとして受け付ける上限。これを超える圧縮データは読み込まない。</summary>
        internal const int MaxUncompressedBytes = 256 * 1024 * 1024;

        /// <summary>展開サイズを確かめながら読む。小さな圧縮データが巨大に膨らむ場合はfalseを返し、読み込みを打ち切る。</summary>
        private static bool TryReadLimited(Stream source, out byte[]? data, int limit = MaxUncompressedBytes)
        {
            data = null;
            using var output = new MemoryStream();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > limit) return false;
                output.Write(buffer, 0, read);
            }
            data = output.ToArray();
            return true;
        }

        /// <summary>ファイルの中身を判定して読み込む。S98は変換してから、VGZは展開してから解析する。</summary>
        public static VgmFile? Load(byte[] fileBytes, string defaultTitle = "")
        {
            if (fileBytes == null || fileBytes.Length < 10) return null;
            // S98はVGMに変換して読み込み、形式名と版を付ける。
            if (fileBytes.AsSpan(0, 3).SequenceEqual("S98"u8))
            {
                try
                {
                    var converted = Load(Music.S98Decoder.Convert(fileBytes, defaultTitle), defaultTitle);
                    if (converted != null)
                    {
                        converted.FormatName = "S98";
                        if (fileBytes.Length > 3 && fileBytes[3] >= '0' && fileBytes[3] <= '3')
                        {
                            converted.S98Version = fileBytes[3] - '0';
                        }
                    }
                    return converted;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException or ArgumentException) { return null; }
            }

            byte[]? uncompressed = null;

            // GZip（VGZ標準のマジック0x1F 0x8B）なら展開する。
            if (fileBytes[0] == 0x1F && fileBytes[1] == 0x8B)
            {
                try
                {
                    using var ms = new MemoryStream(fileBytes);
                    using var gz = new GZipStream(ms, CompressionMode.Decompress);
                    if (!TryReadLimited(gz, out uncompressed)) return null;
                }
                catch
                {
                    // 標準のGZipStreamで失敗したら、SharpCompressのGZipStreamで展開し直す。
                    try
                    {
                        using var ms = new MemoryStream(fileBytes);
                        using var gz = new SharpCompress.Compressors.Deflate.GZipStream(ms, SharpCompress.Compressors.CompressionMode.Decompress);
                        if (!TryReadLimited(gz, out uncompressed)) return null;
                    }
                    catch
                    {
                        // それでも失敗したら、生のVGMデータか確かめる。
                        if (fileBytes.Length >= 64 && fileBytes[0] == 0x56 && fileBytes[1] == 0x67 && fileBytes[2] == 0x6d && fileBytes[3] == 0x20)
                        {
                            uncompressed = fileBytes;
                        }
                    }
                }
            }
            // ZLib・Deflate（0x78 0x01・0x78 0x9C・0x78 0xDA・0x78 0x5E・0x78 0xBBなど）なら展開する。
            else if (fileBytes[0] == 0x78 && (fileBytes[1] == 0x01 || fileBytes[1] == 0x9C || fileBytes[1] == 0xDA || fileBytes[1] == 0x5E || fileBytes[1] == 0xBB))
            {
                try
                {
                    using var ms = new MemoryStream(fileBytes);
                    using var zlib = new SharpCompress.Compressors.Deflate.ZlibStream(ms, SharpCompress.Compressors.CompressionMode.Decompress);
                    if (!TryReadLimited(zlib, out uncompressed)) return null;
                }
                catch
                {
                    uncompressed = fileBytes;
                }
            }
            else
            {
                uncompressed = fileBytes;
            }

            // 展開結果が短すぎる場合は、元のデータが生のVGMか最後に確かめる。
            if (uncompressed == null || uncompressed.Length < 64)
            {
                if (fileBytes.Length >= 64 && fileBytes[0] == 0x56 && fileBytes[1] == 0x67 && fileBytes[2] == 0x6d && fileBytes[3] == 0x20)
                {
                    uncompressed = fileBytes;
                }
                else
                {
                    return null;
                }
            }

            // VGMヘッダーのマジック "Vgm " を確かめてから解析する。
            if (uncompressed[0] != 0x56 || uncompressed[1] != 0x67 ||
                uncompressed[2] != 0x6d || uncompressed[3] != 0x20)
            {
                return null;
            }

            var vgm = new VgmFile(uncompressed);
            vgm.ParseHeader(defaultTitle);
            return vgm;
        }

        /// <summary>VGMヘッダーを解析して、版、各音源チップのクロック、長さ、ループ位置、データ開始位置、GD3タグを読む。</summary>
        private void ParseHeader(string defaultTitle)
        {
            using var reader = new BinaryReader(new MemoryStream(RawData));

            // 版、SN76489・YM2413のクロック、GD3の位置、長さとループ位置を読む。
            reader.BaseStream.Seek(0x08, SeekOrigin.Begin);
            Version = reader.ReadUInt32();

            Sn76489Clock = reader.ReadUInt32();
            Ym2413Clock = reader.ReadUInt32();

            uint gd3RelOffset = reader.ReadUInt32();

            TotalSamples = reader.ReadUInt32();
            uint loopRelOffset = reader.ReadUInt32();
            LoopSamples = reader.ReadUInt32();

            if (loopRelOffset != 0)
            {
                LoopOffset = (int)(0x1C + loopRelOffset);
            }

            // YM2612（0x2C、1.10以降）とYM2151（0x30）のクロック、データ開始位置（0x34）を読む。1.50より前のデータ開始位置は0x40固定。
            reader.BaseStream.Seek(0x2C, SeekOrigin.Begin);
            Ym2612Clock = reader.ReadUInt32();

            Ym2151Clock = reader.ReadUInt32();

            uint dataRelOffset = reader.ReadUInt32();
            if (Version < 0x150 || dataRelOffset == 0)
            {
                DataOffset = 0x40;
            }
            else
            {
                DataOffset = (int)(0x34 + dataRelOffset);
            }

            // 1.01以前はYM2413欄がYM2612・YM2151と共用だった。5MHzを超えればYM2612とみなす。
            if(Version<=0x101) {
                uint shared=Ym2413Clock;Ym2612Clock=Ym2151Clock=0;
                if(shared>5000000){Ym2612Clock=shared;Ym2413Clock=0;}
                else {
                    // 共用欄はそれだけでは区別できないため、命令の境目をたどってYM2151への書込み（0x54）があるか調べる。
                    for(int p=DataOffset;p<RawData.Length;) {
                        byte op=RawData[p];
                        if(op==0x54){Ym2151Clock=shared;Ym2413Clock=0;break;}
                        if(op==0x66)break;
                        int size=op is 0x4f or 0x50?2:op is >=0x51 and <=0x5f or 0x61?3:op is 0x62 or 0x63 or >=0x70 and <=0x8f?1:0;
                        if(size==0 || p>RawData.Length-size)break;p+=size;
                    }
                }
            }

            // 宣言されたヘッダーより後ろの欄は、命令データがその位置まで続いていても0として扱う。
            int headerEnd = Math.Min(DataOffset, RawData.Length);
            if (Version >= 0x170 && headerEnd >= 0xc0)
            {
                uint extra = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(RawData.AsSpan(0xbc, 4));
                if (extra != 0 && extra < headerEnd - 0xbc) headerEnd = 0xbc + (int)extra;
            }
            uint HeaderClock(int offset, uint version = 0x151) => Version >= version &&
                offset + 4 <= headerEnd
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(RawData.AsSpan(offset, 4)) : 0;
            // 1.51以降のヘッダーにある各音源チップのクロックと設定を読む。
            SegaPcmClock = HeaderClock(0x38);
            SegaPcmInterface = HeaderClock(0x3c);
            Rf5C68Clock = HeaderClock(0x40);
            Ym2203Clock = HeaderClock(0x44);
            Ym2608Clock = HeaderClock(0x48);
            Ym2610Clock = HeaderClock(0x4c);
            Ym3812Clock = HeaderClock(0x50);
            Ym3526Clock = HeaderClock(0x54);
            Y8950Clock = HeaderClock(0x58);
            Ymf262Clock = HeaderClock(0x5c);
            Ymf278BClock = HeaderClock(0x60);
            Ymf271Clock = HeaderClock(0x64);
            Ymz280BClock = HeaderClock(0x68);
            Rf5C164Clock = HeaderClock(0x6c);
            PwmClock = HeaderClock(0x70);
            Ay8910Clock = HeaderClock(0x74);
            GameBoyClock = HeaderClock(0x80);
            NesApuClock = HeaderClock(0x84);
            MultiPcmClock = HeaderClock(0x88);
            OkiM6258Clock = HeaderClock(0x90);
            OkiM6258Flags = 0x94 < headerEnd ? RawData[0x94] : (byte)0;
            OkiM6295Clock = HeaderClock(0x98);
            Es550xClock = HeaderClock(0xd0);
            Es5503Clock = HeaderClock(0xcc);
            K051649Clock = HeaderClock(0x9c);
            K054539Clock = HeaderClock(0xa0);
            HuC6280Clock = HeaderClock(0xa4);
            C140Clock = HeaderClock(0xa8);
            C140Type = 0x96 < headerEnd ? RawData[0x96] : (byte)0;
            // C140のクロックが1MHz未満なら、古い記録方法の値を実クロックに直す。
            if (C140Clock != 0)
            {
                uint clockVal = C140Clock & 0x3fffffff;
                if (clockVal < 1000000)
                {
                    uint normalized = (C140Type == 2 && clockVal == 44100) ? 25056500 :
                        (clockVal == 21390 ? 12288000 : clockVal * 576);
                    C140Clock = (C140Clock & ~0x3fffffffu) | normalized;
                }
            }
            K053260Clock = HeaderClock(0xac);
            QSoundClock = HeaderClock(0xb4);
            C352Clock = HeaderClock(0xdc);
            C352ClockDivider = 0xd6 < headerEnd ? RawData[0xd6] : (byte)0;
            IremGa20Clock = HeaderClock(0xe0);

            // 拡張ヘッダー（1.70以降）のクロック表から、2個目のSN76489・YM2203・AY8910のクロックを読む。
            SecondSn76489Clock = Sn76489Clock & 0x3fffffff;
            SecondAy8910Clock = Ay8910Clock & 0x3fffffff;
            SecondYm2203Clock = Ym2203Clock & 0x3fffffff;

            if (Version >= 0x170 && RawData.Length >= 0xc0)
            {
                uint extraRel = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(RawData.AsSpan(0xbc, 4));
                if (extraRel != 0 && 0xbc + extraRel + 8 <= RawData.Length)
                {
                    int extraPos = 0xbc + (int)extraRel;
                    uint extraHdrSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(RawData.AsSpan(extraPos, 4));
                    if (extraHdrSize >= 8 && extraPos + 8 <= RawData.Length)
                    {
                        uint chpClockRel = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(RawData.AsSpan(extraPos + 4, 4));
                        if (chpClockRel != 0)
                        {
                            int clockTablePos = extraPos + 4 + (int)chpClockRel;
                            if (clockTablePos < RawData.Length)
                            {
                                byte entryCount = RawData[clockTablePos];
                                int cur = clockTablePos + 1;
                                for (int i = 0; i < entryCount && cur + 5 <= RawData.Length; i++)
                                {
                                    byte chipId = RawData[cur];
                                    uint clk = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(RawData.AsSpan(cur + 1, 4));
                                    cur += 5;

                                    switch (chipId)
                                    {
                                        case 0x00:
                                            SecondSn76489Clock = clk & 0x3fffffff;
                                            break;
                                        case 0x06:
                                            SecondYm2203Clock = clk & 0x3fffffff;
                                            break;
                                        case 0x12:
                                            SecondAy8910Clock = clk & 0x3fffffff;
                                            break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // GD3タグを解析する。
            if (gd3RelOffset != 0)
            {
                int gd3AbsoluteOffset = (int)(0x14 + gd3RelOffset);
                ParseGd3Tag(gd3AbsoluteOffset);
            }

            // 主要な音源チップを決め、機種名がなければ補う。
            DeterminePrimaryChip();

            if (string.IsNullOrWhiteSpace(TrackName))
            {
                TrackName = !string.IsNullOrWhiteSpace(defaultTitle) ? defaultTitle : "Unknown Track";
            }
        }

        /// <summary>主要な音源チップの名前を決め、GD3に機種名がなければ音源チップから推定して補う。</summary>
        private void DeterminePrimaryChip()
        {
            if (Ym2151Clock != 0)
            {
                PrimaryChipName = "YM2151 (OPM)";
                if (string.IsNullOrWhiteSpace(SystemName))
                {
                    SystemName = OkiM6258Clock != 0 ? "X68000 YM2151+PCM8" : "Arcade YM2151";
                }
            }
            else if (Ym2612Clock != 0)
            {
                PrimaryChipName = "YM2612 (OPN2)";
                if (string.IsNullOrWhiteSpace(SystemName))
                {
                    SystemName = "Mega Drive YM2612";
                }
            }
            else if (Ym2608Clock != 0)
            {
                PrimaryChipName = "YM2608 (OPNA)";
                if (string.IsNullOrWhiteSpace(SystemName))
                {
                    SystemName = "PC-9801 YM2608";
                }
            }
            else if (Ym2610Clock != 0)
            {
                PrimaryChipName = "YM2610 (OPNB)";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Neo Geo YM2610";
            }
            else if (Ym2203Clock != 0)
            {
                PrimaryChipName = "YM2203 (OPN)";
                if (string.IsNullOrWhiteSpace(SystemName))
                {
                    SystemName = "PC-8801 YM2203";
                }
            }
            else if (Ym2413Clock != 0)
            {
                bool vrc7 = IsVrc7;
                PrimaryChipName = vrc7 ? "VRC7" : "YM2413 (OPLL)";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = vrc7 ? "Nintendo Entertainment System" : "OPLL / FM Unit";
            }
            else if (Ymf262Clock != 0 || Ym3812Clock != 0 || Ym3526Clock != 0 || Y8950Clock != 0)
            {
                PrimaryChipName = Ymf262Clock != 0 ? "YMF262 (OPL3)" : Ym3812Clock != 0 ? "YM3812 (OPL2)" :
                    Ym3526Clock != 0 ? "YM3526 (OPL)" : "Y8950 (MSX-AUDIO)";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "OPL FM Sound";
            }
            else if (Ay8910Clock != 0)
            {
                PrimaryChipName = "AY-3-8910";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "AY PSG";
            }
            else if (GameBoyClock != 0)
            {
                PrimaryChipName = "Game Boy DMG";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Nintendo Game Boy";
            }
            else if (NesApuClock != 0)
            {
                PrimaryChipName = "NES APU";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Nintendo Entertainment System";
            }
            else if (Ymz280BClock != 0)
            {
                PrimaryChipName = "YMZ280B";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Arcade PCM";
            }
            else if (OkiM6295Clock != 0)
            {
                PrimaryChipName = "OKIM6295";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Arcade ADPCM";
            }
            else if (PwmClock != 0)
            {
                PrimaryChipName = "Sega PWM";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Sega 32X";
            }
            else if (K051649Clock != 0 || K053260Clock != 0 || K054539Clock != 0)
            {
                PrimaryChipName = K054539Clock != 0 ? "Konami K054539" : K053260Clock != 0 ? "Konami K053260" : "Konami SCC";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Konami Arcade / SCC";
            }
            else if (Es550xClock != 0)
            {
                PrimaryChipName = (Es550xClock & 0x80000000) != 0 ? "ES5506" : "ES5505";
            }
            else if (C140Clock != 0 || C352Clock != 0)
            {
                PrimaryChipName = C352Clock != 0 ? "Namco C352" : "Namco C140";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Namco Arcade";
            }
            else if (Ymf278BClock != 0 || Ymf271Clock != 0)
            {
                PrimaryChipName = Ymf278BClock != 0 ? "YMF278B (OPL4)" : "YMF271 (OPX)";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Yamaha FM/PCM";
            }
            else if (Rf5C68Clock != 0 || Rf5C164Clock != 0 || MultiPcmClock != 0)
            {
                PrimaryChipName = MultiPcmClock != 0 ? "Sega MultiPCM" : Rf5C164Clock != 0 ? "RF5C164" : "RF5C68";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Sega PCM";
            }
            else if (QSoundClock != 0)
            {
                PrimaryChipName = "Capcom QSound";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Capcom Arcade";
            }
            else if (IremGa20Clock != 0)
            {
                PrimaryChipName = "Irem GA20";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Irem Arcade";
            }
            else if (Sn76489Clock != 0)
            {
                PrimaryChipName = "SN76489 (PSG)";
                if (string.IsNullOrWhiteSpace(SystemName))
                {
                    SystemName = "Master System SN76489";
                }
            }
            else if (SegaPcmClock != 0)
            {
                PrimaryChipName = "SegaPCM";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "Sega Arcade PCM";
            }
            else if (OkiM6258Clock != 0)
            {
                PrimaryChipName = "OKIM6258";
                if (string.IsNullOrWhiteSpace(SystemName))
                {
                    SystemName = "X68000 ADPCM";
                }
            }
            else if (HuC6280Clock != 0)
            {
                PrimaryChipName = "HuC6280 PSG";
                if (string.IsNullOrWhiteSpace(SystemName)) SystemName = "PC Engine / TurboGrafx-16";
            }
            else
            {
                PrimaryChipName = "FM/PSG";
                if (string.IsNullOrWhiteSpace(SystemName))
                {
                    SystemName = "Retro Sound Processor";
                }
            }
        }

        /// <summary>GD3タグから曲名、ゲーム名、機種名、作者、メモを読む。曲名・ゲーム名・作者は日本語を、機種名は英語を優先する。</summary>
        private void ParseGd3Tag(int gd3Offset)
        {
            if (gd3Offset + 12 > RawData.Length) return;

            // マジック "Gd3 " を確かめ、長さ欄（uint32）の後ろから文字列を読む。
            if (RawData[gd3Offset] != 0x47 || RawData[gd3Offset + 1] != 0x64 ||
                RawData[gd3Offset + 2] != 0x33 || RawData[gd3Offset + 3] != 0x20)
            {
                return;
            }

            int pos = gd3Offset + 12;

            string enTitle = ReadUtf16String(ref pos);
            string jpTitle = ReadUtf16String(ref pos);
            string enGame = ReadUtf16String(ref pos);
            string jpGame = ReadUtf16String(ref pos);
            string enSystem = ReadUtf16String(ref pos);
            string jpSystem = ReadUtf16String(ref pos);
            string enAuthor = ReadUtf16String(ref pos);
            string jpAuthor = ReadUtf16String(ref pos);
            string releaseDate = ReadUtf16String(ref pos);
            string convertedBy = ReadUtf16String(ref pos);
            string notes = ReadUtf16String(ref pos);

            TrackName = !string.IsNullOrWhiteSpace(jpTitle) ? jpTitle : enTitle;
            GameName = !string.IsNullOrWhiteSpace(jpGame) ? jpGame : enGame;
            SystemName = !string.IsNullOrWhiteSpace(enSystem) ? enSystem : jpSystem;
            Author = !string.IsNullOrWhiteSpace(jpAuthor) ? jpAuthor : enAuthor;
            Notes = notes;
        }

        /// <summary>
        /// VGM・VGZ のストリームから GD3 の曲名だけを読む（曲リストの表示用）。日本語の曲名を優先し、なければ英語の曲名。
        /// 曲データは読み飛ばし、全体を展開しない。読めなければ空文字。
        /// </summary>
        public static string ReadGd3Title(Stream input)
        {
            try
            {
                // 先頭2バイトで GZip かどうかを見分け、VGZ なら展開しながら読む。
                var head = new byte[2];
                if (ReadFully(input, head) < 2) return "";
                Stream source;
                if (head[0] == 0x1f && head[1] == 0x8b)
                {
                    if (input.CanSeek) input.Position -= 2;
                    else input = new PrefixedStream(head, input);
                    source = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
                    head = new byte[2];
                    if (ReadFully(source, head) < 2) return "";
                }
                else source = input;
                using var _ = ReferenceEquals(source, input) ? null : source;

                // ヘッダーの 0x14 にある GD3 の相対位置まで読み、そこまで読み飛ばす。
                var header = new byte[0x18];
                head.CopyTo(header, 0);
                if (head[0] != (byte)'V' || head[1] != (byte)'g' || ReadFully(source, header.AsSpan(2)) < header.Length - 2) return "";
                uint relative = BitConverter.ToUInt32(header, 0x14);
                if (relative == 0) return "";
                long target = 0x14L + relative, position = header.Length;
                if (target < position || target > 256L * 1024 * 1024) return "";
                if (source.CanSeek) source.Position = target;
                else
                {
                    var skip = new byte[81920];
                    while (position < target)
                    {
                        int read = source.Read(skip, 0, (int)Math.Min(skip.Length, target - position));
                        if (read <= 0) return "";
                        position += read;
                    }
                }

                // "Gd3 "・版・長さの後ろの、英語と日本語の曲名を読む。
                var tag = new byte[12];
                if (ReadFully(source, tag) < 12 || tag[0] != 0x47 || tag[1] != 0x64 || tag[2] != 0x33 || tag[3] != 0x20) return "";
                int length = (int)Math.Min(BitConverter.ToUInt32(tag, 8), 64 * 1024);
                var body = new byte[length];
                length = ReadFully(source, body);
                int pos = 0;
                string english = ReadUtf16(body, length, ref pos), japanese = ReadUtf16(body, length, ref pos);
                return (!string.IsNullOrWhiteSpace(japanese) ? japanese : english).Trim();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ObjectDisposedException)
            {
                return "";
            }
        }

        private static int ReadFully(Stream input, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = input.Read(buffer[total..]);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static string ReadUtf16(byte[] data, int length, ref int pos)
        {
            int start = pos;
            while (pos + 1 < length)
            {
                if (data[pos] == 0 && data[pos + 1] == 0) { string text = Encoding.Unicode.GetString(data, start, pos - start); pos += 2; return text; }
                pos += 2;
            }
            pos = length;
            return "";
        }

        /// <summary>先に読んでしまった数バイトを先頭に戻して読ませる、読み取り専用のストリーム。</summary>
        private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
        {
            private int _offset;
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_offset < prefix.Length)
                {
                    int n = Math.Min(count, prefix.Length - _offset);
                    Array.Copy(prefix, _offset, buffer, offset, n);
                    _offset += n;
                    return n;
                }
                return inner.Read(buffer, offset, count);
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>NUL終端のUTF-16LE文字列を1個読み、位置を終端の後ろへ進める。</summary>
        private string ReadUtf16String(ref int pos)
        {
            if (pos >= RawData.Length) return "";

            int start = pos;
            while (pos + 1 < RawData.Length)
            {
                if (RawData[pos] == 0 && RawData[pos + 1] == 0)
                {
                    int len = pos - start;
                    pos += 2;
                    if (len <= 0) return "";
                    return Encoding.Unicode.GetString(RawData, start, len);
                }
                pos += 2;
            }
            return "";
        }
    }
}
