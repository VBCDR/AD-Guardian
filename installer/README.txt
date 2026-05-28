AD Guardian installer source of truth
====================================

This folder now uses Inno Setup as the active installer path:

- AD Guardian Installer.iss

Legacy Visual Studio Installer Project files were retired and archived as:

- AD Guardian Installer.vdproj.legacy

Build the installer from the AD-Guardian repo with:

powershell -ExecutionPolicy Bypass -File .\scripts\build-distributions.ps1 -Portable -Installer

Expected output:

- Release\AD-Guardian-Setup-<version>.exe
- Release\setup.exe
