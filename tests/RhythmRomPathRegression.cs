using System.IO;
using MirrorFlux.Services.Music;

/// <summary>YM2608 のリズム用 ROM を探す場所（EXE のフォルダー、その下の roms フォルダー）の検査。</summary>
internal static class RhythmRomPathRegression
{
    public static void Run(string root, Action<bool, string> check)
    {
        string exe = Path.Combine(root, "rhythm-rom-path");
        string roms = Path.Combine(exe, "roms");
        Directory.CreateDirectory(roms);
        try
        {
            check(Ym2608RhythmRom.FindPath(exe) == null, "YM2608 rhythm ROM is not found when neither folder has it");

            string inRoms = Path.Combine(roms, "ym2608_adpcm.rom");
            File.WriteAllBytes(inRoms, new byte[8192]);
            check(string.Equals(Ym2608RhythmRom.FindPath(exe), inRoms, StringComparison.OrdinalIgnoreCase),
                "YM2608 rhythm ROM is found in the roms folder, ignoring letter case");

            string beside = Path.Combine(exe, "YM2608_ADPCM.ROM");
            File.WriteAllBytes(beside, new byte[8192]);
            check(string.Equals(Ym2608RhythmRom.FindPath(exe), beside, StringComparison.OrdinalIgnoreCase),
                "YM2608 rhythm ROM next to the EXE takes priority over the roms folder");
        }
        finally { Directory.Delete(exe, true); }
    }
}