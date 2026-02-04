# PeekVri Web Switcher (WinForms)

A small admin WinForms tool to:
- Discover installed PeekVri UK environments: `C:\PeekVri_UK_*`
- Show which SRM2 **EN** web content is currently active (directory checksum)
- Switch between different webpage packs by copying a selected pack into:
  - `C:\{environment}\PTC-1\webserver\srm2\EN` (preferred)
  - or `C:\{environment}\webserver\srm2\EN` (older installs)

## What’s included
Two embedded webpage packs (as zip resources):
- `MCA_Webpages_20260224.zip`
- `Swarco_Default_20260224.zip`

They are extracted to:
`C:\ProgramData\PeekVriWebSwitcher\Packages\...`

You can also add more packs at runtime by selecting a zip (e.g. `loadweb.zip`).

## Build
Open in Visual Studio 2022+ (or use dotnet CLI):

```powershell
dotnet restore
dotnet build -c Release
```

## Run
Run as Administrator (the app manifest requests admin) because it writes under `C:\PeekVri_UK_*`.

## Notes on ZIP structure
The app accepts zips that contain one of:
- `webserver\srm2\EN\...`
- `srm2\EN\...`
- or where the zip root is already the EN content (e.g. contains `frames\` / `editor\` / `index.html`).

If a zip does not match, it will throw an error with guidance.

## Backups
Before activating a pack, the current EN folder is backed up to:

`C:\ProgramData\PeekVriWebSwitcher\Backups\<environment>\<timestamp>\...`
