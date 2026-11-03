namespace PS3Transfer.Core;

public static class PlayNotes
{
    public static (string Title, string Body) Describe(ContentKind kind, bool split, string fileSystem)
    {
        var fat = Ps3Layout.IsFat(fileSystem);
        return kind switch
        {
            ContentKind.Ps3Iso when fat => ("Plays from the USB drive",
                "The ISO goes in \\PS3ISO. Pieces over 4 GB are named .iso.0, .iso.1, and so on, which is the split webMAN MOD, multiMAN, and IRISMAN mount as one game.\n\nPlug the drive into the PS3 and launch it from webMAN Games. If the list is stale, hold SELECT and choose Refresh."),
            ContentKind.Ps3Iso => ("Needs prepISO on the PS3",
                "The ISO is copied as one file into \\PS3ISO. The PS3 XMB does not mount this file system. Run prepISO, then refresh webMAN Games and launch the game."),
            ContentKind.Ps2Iso when split => ("Join this PS2 ISO on the console",
                "PS2 games run from the internal drive. This ISO is split into \\PS2ISO so it fits on FAT32. webMAN often skips split PS2 ISOs.\n\nIn multiMAN's file manager, copy the .iso.0 file to dev_hdd0/PS2ISO. multiMAN joins the parts. Launch that internal ISO. HEN cannot play a raw PS2 ISO; it needs a PS2 Classics BIN.ENC package."),
            ContentKind.Ps2Iso when fat => ("PS2 ISO, copied to the internal drive when launched",
                "The file is in \\PS2ISO. On CFW, launch it from webMAN and the console copies it to the internal drive, then starts it. The PS3 needs enough free internal space for the whole ISO.\n\nHEN cannot play a raw PS2 ISO. Encrypt it as BIN.ENC with PS2 Classics first."),
            ContentKind.Ps2Iso => ("PS2 ISO on a drive prepISO can scan",
                "The file is in \\PS2ISO. Run prepISO. Launching it from webMAN copies it onto the internal drive. HEN needs a BIN.ENC package instead of a raw ISO."),
            ContentKind.Ps1Disc => ("Plays from \\PSXISO",
                "CUE and BIN names are left unchanged, so the CUE still points at the right files. Launch the disc from webMAN."),
            ContentKind.PspIso => ("Plays from \\PSPISO",
                "Launch it from webMAN with the PSP Minis or Remasters launcher installed."),
            ContentKind.Pkg when fat && split => ("These parts will not launch from the SSD",
                "The pieces are one installer. FAT32 cannot store it as a single file, and webMAN cannot mount .pkg.66600 parts. They are placed in \\packages\\<name> so multiMAN can join them onto the internal drive. Package Manager then installs the game there. It does not run from this SSD.\n\nAn extracted package folder is the copy that launches from the drive. Add that folder and it goes in \\GAMEI."),
            ContentKind.Pkg when fat => ("Install from the SSD",
                "The package is one file in \\packages, with any split parts joined first. On the PS3 open Game → Package Manager → Install Package Files → Standard.\n\nTo keep the installed files on this SSD, hold SELECT and press SQUARE in webMAN, enable external game data, install, then turn it off. Every file in the package has to fit on FAT32. A matching RAP in \\exdata is activated with PSNPatch or webMAN."),
            ContentKind.Pkg => ("Parts joined into one package",
                "The pieces are joined into one file in \\PKG. Run prepISO, mount the PKG from webMAN, and install it with Package Manager. That install uses the internal drive.\n\nTo launch a package from the SSD itself, add the extracted package folder. It is copied to \\GAMEI, which webMAN mounts."),
            ContentKind.PkgFolder => ("Plays from the SSD",
                "This package is already extracted, so it is copied to \\GAMEI\\<title id> with the original file names. webMAN mounts that folder and launches the game from the drive.\n\nPlug in the SSD. In webMAN Setup, under Scan for content, enable GAMEI and app_home. Open webMAN Setup → Files, open the USB drive, open GAMEI, highlight the game folder, and press X."),
            ContentKind.Rap or ContentKind.Edat => ("License file in \\exdata",
                "The file name is unchanged, which is what the console looks up. On FAT32, PSNPatch and reactPSN read \\exdata from USB. Activate the license, then install the matching PKG."),
            ContentKind.Ps3Folder when split => ("Folder game in \\GAMES",
                "webMAN and multiMAN launch this like a disc. Files over 4 GB are stored as .66600 parts next to the original name. Cobra, webMAN, and multiMAN open those parts as one file."),
            ContentKind.Ps3Folder => ("Folder game in \\GAMES",
                "webMAN and multiMAN launch this like a disc. Every file fits on FAT32, so nothing is split."),
            ContentKind.BdIso => ("Blu-ray ISO in \\BDISO",
                fat
                    ? "webMAN lists this under Blu-ray. Splits use the same .iso.0 naming as game ISOs."
                    : "Run prepISO, then open it from webMAN's Blu-ray list."),
            ContentKind.DvdIso => ("DVD ISO in \\DVDISO",
                fat
                    ? "webMAN lists this with DVDs."
                    : "Run prepISO, then open it from webMAN."),
            ContentKind.Rom => ("ROM in \\ROMS",
                "The file keeps its name and is copied into \\ROMS\\<system>. webMAN launches that folder with RetroArch."),
            _ => ("Choose a type", "The file was not recognized. Pick PS3, PS2, PS1, PSP, PKG, or a ROM system before copying."),
        };
    }

    public static (string Title, string Body) DescribeRom(string folder, string label, bool listedByWebMan, bool fat)
    {
        var launch = listedByWebMan
            ? "webMAN lists \\ROMS\\" + folder + " and launches it with RetroArch. In webMAN Setup, enable ROMS under Scan for content, plug in the drive, hold SELECT, and refresh."
            : "webMAN does not list this system. Open RetroArch, choose Load Content, and open the USB drive at ROMS/" + folder + ".";
        var body = "This " + label + " ROM keeps its file name and is copied to \\ROMS\\" + folder + ". " + launch
            + "\n\nRetroArch cannot open a ROM that was split to fit FAT32. BIOS files, when a core needs them, stay in RetroArch's system folder on the internal drive.";
        if (!fat)
            body += "\n\nThis drive is not FAT32. Run prepISO so the PS3 can see the files.";
        return ("Plays in RetroArch", body);
    }
}
