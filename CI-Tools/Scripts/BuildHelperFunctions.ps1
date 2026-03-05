##########################
# Build Helper Functions
##########################

function Build-Solution {
    Param(
        $SolutionPath, 
        $MSBuildConfiguration, 
        $MsBuildPlatform, 
        $MsBuildVisualStudioVersion, 
        $MsBuildVerbosity)

    Write-Host "Building: '$SolutionPath'..."  -ForegroundColor White

    & $msBuildLocation $SolutionPath `
            /p:configuration=$MSBuildConfiguration `
            /p:Platform=$MsBuildPlatform `
            /p:VisualStudioVersion=$MsBuildVisualStudioVersion `
            /verbosity:$MsBuildVerbosity `
            /t:Build

    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    Write-Host
}

function Rebuild-Solution {
    Param(
        $SolutionPath, 
        $MSBuildConfiguration, 
        $MsBuildPlatform, 
        $MsBuildVisualStudioVersion, 
        $MsBuildVerbosity)

        Clean-Solution `
            -SolutionPath $SolutionPath `
            -MSBuildConfiguration $MSBuildConfiguration `
            -MsBuildPlatform $MsBuildPlatform `
            -MsBuildVisualStudioVersion $MsBuildVisualStudioVersion `
            -MsBuildVerbosity $MsBuildVerbosity

        Build-Solution `
            -SolutionPath $SolutionPath `
            -MSBuildConfiguration $MSBuildConfiguration `
            -MsBuildPlatform $MsBuildPlatform `
            -MsBuildVisualStudioVersion $MsBuildVisualStudioVersion `
            -MsBuildVerbosity $MsBuildVerbosity
}

function Execute-UnitTests {
    Param($ProjectFiles)
    
    Write-Host "Running tests on '$ProjectFiles'"
 
    & $XUnitTestLocation $ProjectFiles
    
    if($LASTEXITCODE -ne 0) { throw "Unit tests failed" }
}

function Clean-Solution {
    Param(
        $SolutionPath, 
        $MSBuildConfiguration, 
        $MsBuildPlatform, 
        $MsBuildVisualStudioVersion, 
        $MsBuildVerbosity)

    Write-Host "Cleaning: '$SolutionPath'..."  -ForegroundColor White

    & $msBuildLocation  $SolutionPath `
            /p:configuration=$MSBuildConfiguration `
            /p:Platform=$MsBuildPlatform `
            /p:VisualStudioVersion=$MsBuildVisualStudioVersion `
            /verbosity:$MsBuildVerbosity `
            /t:Clean

    if($LASTEXITCODE -ne 0) { throw "Build failed." }

    Write-Host
}

function Run-NugetRestore {    
    Param(
        $NugetLocation,
        $SolutionPath)

    if (Test-Path -Path $NugetPackageLocation)
    {
        Write-Host "Deleting NuGet packages..."
        Remove-Item -Path $NugetPackageLocation -Force -Recurse
    }

    Write-Host "Downloading solution nuget packages... $NugetLocation $SolutionPath" -ForegroundColor White
    & $NugetLocation restore "$SolutionPath"
    Write-Host
}

function Run-TestsByFilter {
    Param($Filter)

        $xunitConsoleDirectory = "$NuGetPackagesDirectory\xunit.runner.console"
        $xunitConsolePath = (Get-ChildItem -Path $xunitConsoleDirectory -Filter xunit.console.exe -Recurse | 
                                            Select-Object -First 1).FullName

        $testDirectory = Resolve-Path -path "$CIRoot\..\Test"

        $testAssemblies = Get-ChildItem -Path $testDirectory -filter $Filter -Recurse | 
                                Select-Object -ExpandProperty FullName | 
                                where {$_ -like "*bin*"}      

        & $xunitConsolePath $testAssemblies
}

function Set-AssemblyFileVersion([string]$pathToFile, [string]$majorVer, [string]$minorVer, [string]$buildVer, [string]$revVer) {

    #load the file and process the lines
    $newFile = Get-Content $pathToFile -encoding "UTF8" | foreach-object {
        if ($_.StartsWith("[assembly: AssemblyFileVersion")) {
            $verStart = $_.IndexOf("(")
            $verEnd = $_.IndexOf(")", $verStart)
            $origVersion = $_.SubString($verStart+2, $verEnd-$verStart-3)
            
            $segments=$origVersion.Split(".")
            
            #default values for each segment
            $v1="1"
            $v2="0"
            $v3="0"
            $v4="0"
            
            #assign them based on what was found
            if ($segments.Length -gt 0) { $v1=$segments[0] }
            if ($segments.Length -gt 1) { $v2=$segments[1] } 
            if ($segments.Length -gt 2) { $v3=$segments[2] } 
            if ($segments.Length -gt 3) { $v4=$segments[3] } 
            
            $v1 = Assign-VersionValue $v1 $majorVer
            $v2 = Assign-VersionValue $v2 $minorVer
            $v3 = Assign-VersionValue $v3 $buildVer
            $v4 = Assign-VersionValue $v4 $revVer
            
            if ($v1 -eq $null) { throw "Major version CANNOT be blank!" }
            if ($v2 -eq $null) { throw "Minor version CANNOT be blank!" }
            
            $newVersion = "$v1.$v2"
            
            if ($v3 -ne $null) {
                $newVersion = "$newVersion.$v3"
                
                if ($v4 -ne $null) {
                    $newVersion = "$newVersion.$v4"
                }
            }

            write-host "$msgPrefix Setting AssemblyFileVersion to $newVersion"
            $_.Replace($origVersion, $newVersion)
        }  else {
            $_
        } 
    }
    
    $newfile | set-Content $pathToFile -encoding "UTF8"
}