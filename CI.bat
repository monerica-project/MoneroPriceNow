@echo off

echo CryptoPriceNow CI - Version 1.1.2
echo Copyright CryptoPriceNow (tm) - All right reserved.

powershell -command "if (!(Get-Module psake -ListAvailable)) { if (!(Get-Module PsGet -ListAvailable)) { Find-Module -Name 'psake' | Save-Module -Path; Install-Module 'psake'}}"

echo User Profile Path: %UserProfile%

if "%1" == "" (
	%UserProfile%\Documents\WindowsPowerShell\Modules\psake\4.9.0\psake.cmd ".\CI-Tools\CI-Main.ps1 -framework 4.5.1 -docs -nologo"
	%UserProfile%\Documents\WindowsPowerShell\Modules\psake\4.9.0\psake.cmd ".\CI-Tools\CI-Main.ps1 -framework 4.5.1 -nologo"
) else (
	%UserProfile%\Documents\WindowsPowerShell\Modules\psake\4.9.0\psake.cmd ".\CI-Tools\CI-Main.ps1 -framework 4.5.1 %* -nologo"
)