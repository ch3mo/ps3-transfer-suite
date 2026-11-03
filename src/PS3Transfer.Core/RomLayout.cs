using System.Text;

namespace PS3Transfer.Core;

/// <summary>
/// Folder names webMAN MOD scans under \ROMS and hands to RetroArch through PKGLAUNCH.
/// A system that webMAN does not list is still copied there so RetroArch can open it with Load Content.
/// </summary>
public sealed record RomSystem(string Folder, string Label, bool ListedByWebMan, IReadOnlyList<string> Extensions);

public readonly record struct RomHit(string Folder, string? Warning);

public static class RomLayout
{
    public const string Root = "ROMS";

    public static readonly IReadOnlyList<RomSystem> Systems =
    [
        new("NES", "NES", true, [".nes", ".unf", ".unif", ".fds"]),
        new("SNES", "Super Nintendo", true, [".sfc", ".smc", ".swc", ".fig"]),
        new("GB", "Game Boy", true, [".gb"]),
        new("GBC", "Game Boy Color", true, [".gbc"]),
        new("GBA", "Game Boy Advance", true, [".gba"]),
        new("GENESIS", "Genesis", true, [".md", ".gen", ".smd", ".sms"]),
        new("GG", "Game Gear", true, [".gg"]),
        new("PICO", "Sega 32X", true, [".32x"]),
        new("PCE", "PC Engine", true, [".pce"]),
        new("NGP", "Neo Geo Pocket", true, [".ngp", ".ngc"]),
        new("ATARI2600", "Atari 2600", true, [".a26"]),
        new("ATARI5200", "Atari 5200", true, [".a52"]),
        new("ATARI7800", "Atari 7800", true, [".a78"]),
        new("LYNX", "Lynx", true, [".lnx"]),
        new("JAGUAR", "Jaguar", true, [".j64", ".jag"]),
        new("VBOY", "Virtual Boy", true, [".vb", ".vboy"]),
        new("WSWAM", "WonderSwan", true, [".ws", ".wsc"]),
        new("POKEMINI", "Pokemon Mini", true, [".min"]),
        new("N64", "Nintendo 64", false, [".n64", ".z64", ".v64"]),
        new("NDS", "Nintendo DS", false, [".nds"]),
        new("MAME", "Arcade (MAME)", true, []),
        new("FBA", "Arcade (FBNeo)", true, []),
        new("DOOM", "Doom", true, [".wad"]),
        new("AMIGA", "Amiga", true, [".adf", ".adz", ".ipf"]),
        new("VICE", "Commodore 64", true, [".d64", ".t64", ".prg", ".crt", ".g64"]),
        new("FMSX", "MSX", true, [".mx1", ".mx2", ".rom"]),
        new("FUSE", "ZX Spectrum", true, [".z80", ".tzx", ".sna", ".tap"]),
        new("INTV", "Intellivision", true, [".int"]),
        new("VECX", "Vectrex", true, [".vec"]),
        new("GW", "Game & Watch", true, [".mgw"]),
    ];

    private static readonly Dictionary<string, RomSystem> ByExtension = BuildExtensions();
    private static readonly Dictionary<string, RomSystem> ByAlias = BuildAliases();

    public static RomSystem? Find(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;
        foreach (var system in Systems)
        {
            if (system.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))
                return system;
        }

        return null;
    }

    public static bool IsRomName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return true;
        return ByExtension.ContainsKey(ext);
    }

    public static bool TryClassify(string fileName, string? directory, string? fullPath, out RomHit hit)
    {
        hit = default;
        var ext = Path.GetExtension(fileName);
        if (ByExtension.TryGetValue(ext, out var system))
        {
            hit = new RomHit(system.Folder, null);
            return true;
        }

        var archive = ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        if (!archive && !AliasOf(directory, out _))
            return false;

        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) && fullPath is not null)
        {
            foreach (var name in ZipEntryNames(fullPath))
            {
                if (ByExtension.TryGetValue(Path.GetExtension(name), out var inner))
                {
                    hit = new RomHit(inner.Folder, null);
                    return true;
                }
            }
        }

        if (AliasOf(directory, out var aliased) && aliased is not null)
        {
            hit = new RomHit(aliased.Folder, null);
            return true;
        }

        if (!archive)
            return false;

        hit = new RomHit("MAME", "Treated as an arcade ROM. Pick another system if this archive is a zipped console game.");
        return true;
    }

    private static bool AliasOf(string? directory, out RomSystem? system)
    {
        system = null;
        if (string.IsNullOrEmpty(directory))
            return false;
        var name = Path.GetFileName(directory).Trim();
        if (name.Length == 0)
            return false;
        return ByAlias.TryGetValue(name, out system);
    }

    private static Dictionary<string, RomSystem> BuildExtensions()
    {
        var map = new Dictionary<string, RomSystem>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in Systems)
        {
            foreach (var ext in system.Extensions)
                map[ext] = system;
        }

        return map;
    }

    private static Dictionary<string, RomSystem> BuildAliases()
    {
        var map = new Dictionary<string, RomSystem>(StringComparer.OrdinalIgnoreCase);
        void Add(string alias, string folder)
        {
            if (map.ContainsKey(alias))
                return;
            var system = Find(folder) ?? throw new InvalidOperationException("Unknown ROM folder " + folder);
            map[alias] = system;
        }

        foreach (var system in Systems)
            Add(system.Folder, system.Folder);

        Add("snes", "SNES");
        Add("super nintendo", "SNES");
        Add("super famicom", "SNES");
        Add("nes", "NES");
        Add("famicom", "NES");
        Add("gameboy", "GB");
        Add("game boy", "GB");
        Add("gameboy color", "GBC");
        Add("game boy color", "GBC");
        Add("gameboy advance", "GBA");
        Add("game boy advance", "GBA");
        Add("genesis", "GENESIS");
        Add("megadrive", "GENESIS");
        Add("mega drive", "GENESIS");
        Add("master system", "GENESIS");
        Add("sms", "GENESIS");
        Add("game gear", "GG");
        Add("gamegear", "GG");
        Add("32x", "PICO");
        Add("picodrive", "PICO");
        Add("pc engine", "PCE");
        Add("pcengine", "PCE");
        Add("turbografx", "PCE");
        Add("tg16", "PCE");
        Add("neo geo pocket", "NGP");
        Add("neogeo pocket", "NGP");
        Add("atari 2600", "ATARI2600");
        Add("atari 5200", "ATARI5200");
        Add("atari 7800", "ATARI7800");
        Add("2600", "ATARI2600");
        Add("wonder swan", "WSWAM");
        Add("wonderswan", "WSWAM");
        Add("virtual boy", "VBOY");
        Add("nintendo 64", "N64");
        Add("n64", "N64");
        Add("nintendo ds", "NDS");
        Add("nds", "NDS");
        Add("mame", "MAME");
        Add("arcade", "MAME");
        Add("fba", "FBA");
        Add("fbneo", "FBA");
        Add("fb neo", "FBA");
        Add("doom", "DOOM");
        Add("amiga", "AMIGA");
        Add("c64", "VICE");
        Add("commodore", "VICE");
        Add("msx", "FMSX");
        Add("zx spectrum", "FUSE");
        Add("spectrum", "FUSE");
        Add("intellivision", "INTV");
        Add("vectrex", "VECX");
        Add("pokemon mini", "POKEMINI");
        return map;
    }

    private static List<string> ZipEntryNames(string path)
    {
        var names = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 22)
                return names;
            var tailLen = (int)Math.Min(stream.Length, 65557);
            var tail = new byte[tailLen];
            stream.Seek(-tailLen, SeekOrigin.End);
            if (stream.Read(tail, 0, tailLen) != tailLen)
                return names;

            var eocd = -1;
            for (var i = tail.Length - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                {
                    eocd = i;
                    break;
                }
            }

            if (eocd < 0)
                return names;
            var count = BitConverter.ToUInt16(tail, eocd + 10);
            var size = BitConverter.ToUInt32(tail, eocd + 12);
            var offset = BitConverter.ToUInt32(tail, eocd + 16);
            if (count == 0 || size is 0 or > 2_000_000 || offset > stream.Length)
                return names;

            var central = new byte[size];
            stream.Position = offset;
            if (stream.Read(central, 0, central.Length) != central.Length)
                return names;

            var cursor = 0;
            var limit = Math.Min(count, (ushort)40);
            for (var n = 0; n < limit && cursor + 46 <= central.Length; n++)
            {
                if (central[cursor] != 0x50 || central[cursor + 1] != 0x4B || central[cursor + 2] != 0x01 || central[cursor + 3] != 0x02)
                    break;
                var nameLen = BitConverter.ToUInt16(central, cursor + 28);
                var extra = BitConverter.ToUInt16(central, cursor + 30);
                var comment = BitConverter.ToUInt16(central, cursor + 32);
                var nameStart = cursor + 46;
                if (nameStart + nameLen > central.Length)
                    break;
                var name = Encoding.UTF8.GetString(central, nameStart, nameLen).Replace('\\', '/');
                if (name.Length > 0 && !name.EndsWith('/'))
                    names.Add(name);
                cursor = nameStart + nameLen + extra + comment;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return names;
        }

        return names;
    }
}
