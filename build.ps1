$ErrorActionPreference = "Stop"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

& $dotnet test "PS3TransferSuite.sln" -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish "src\PS3Transfer.App\PS3Transfer.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
exit $LASTEXITCODE
