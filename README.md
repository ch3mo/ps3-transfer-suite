# PS3 Transfer Suite

![PS3 Transfer Suite](assets/logo.jpg)

Copies games from a Windows PC onto a USB drive so a jailbroken PS3 can use them. The console needs custom firmware or HEN, plus webMAN MOD. This app only fills the drive. It does not modify the PS3, and it never formats a disk.

FAT32 cannot store a file over 4 GB. Files that webMAN can mount are split at 4,294,901,760 bytes (`0xFFFF0000`), the same size splitps3iso uses.

## What plays from the SSD

| What you add | Where it lands | On the PS3 |
| --- | --- | --- |
| PS3 ISO, including pieces over 4 GB | `\PS3ISO`, as `.iso.0`, `.iso.1`, … | webMAN Games. Hold SELECT and refresh if the list is stale. |
| Extracted package folder (`PARAM.SFO` and `USRDIR`) | `\GAMEI\<title id>\` | webMAN Setup → Scan for content → enable GAMEI and app_home. Then Setup → Files → the USB drive → GAMEI → highlight the folder → X. The game launches from the SSD. |
| PKG that fits in one file | `\packages` on FAT32, `\PKG` on NTFS or exFAT | Package Manager installs it. On FAT32, hold SELECT+SQUARE in webMAN and enable external game data before installing if the game should stay on the SSD. |
| Numbered PKG parts (`.pkg.1`, `.pkg.001`, `.pkg.66600`, `name_1.pkg`) | Joined into that one file when the drive can hold it | Same install step. Pieces already on the drive are offered as a merge. The parts are not left for you to join by hand. |

An extracted package is the PKG layout webMAN mounts directly. A `.pkg` file, even after the parts are joined, is still an installer.

## What cannot launch from a FAT32 SSD

A package bigger than 4 GB cannot be one file on FAT32, and webMAN will not mount `.pkg.66600` pieces or a split file inside `\GAMEI`. Those pieces are stored in `\packages\<name>\` so multiMAN can join them onto the internal drive, and Package Manager installs the game there. It does not run from the SSD. Copy that package to an NTFS drive instead: the suite joins it into `\PKG`, and prepISO lets the PS3 see it.

A file inside an extracted package that is over 4 GB is refused on FAT32 for the same reason. Put that folder on NTFS, or use a PS3 ISO.

## Other copies

- PS1 BIN/CUE keeps its original names in `\PSXISO`. A member over 4 GB is refused, because splitting it would break the CUE.
- PS2 ISOs go in `\PS2ISO`. On CFW, webMAN copies a single ISO under 4 GB to the internal drive when you launch it. Split PS2 ISOs usually need multiMAN to join them into `dev_hdd0/PS2ISO`. HEN cannot play a raw PS2 ISO.
- RAP and EDAT files keep their original names in `\exdata`. Activate them with PSNPatch or webMAN.
- NTFS and exFAT are not mounted by the XMB. Run prepISO, then refresh webMAN. The PS3 expects an MBR partition table and, for FAT32, clusters of 32 KB or smaller. The drive list says when that is not true.

## On the PC

- **Transfer** queues ISOs, PKG parts, folder games, and extracted packages. Each row says where the files will go and how to start the game.
- **On the drive** lists what is already in the PS3 folders. Removing an entry deletes only those files, after a confirmation.
- **Join splits** rebuilds one file from `.iso.0`, `.pkg.1`, `name_1.pkg`, or `.66600` pieces onto a disk that can hold it.
- **On the drive** recognizes `name_1.pkg`, `name_2.pkg`, and the rest as one package and offers to merge them. A result that fits is one file in `\packages` or `\PKG`. A larger package on FAT32 is stored as `.pkg.66600` parts so multiMAN can install it.
- A stopped copy can be resumed. The resume record stays in `%AppData%\PS3TransferSuite`, not on the USB drive.
- Verify CRC is off unless you turn it on. Extra split pieces can be marked hidden so Explorer shows the first one.

## Build

The .NET 8 SDK is required.

```
powershell -File build.ps1
```

The script tests the library and publishes `dist\PS3TransferSuite.exe`.
