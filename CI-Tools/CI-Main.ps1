####################################################
# DirectoryManager CI Tool
#
# Example use: .\CI.bat TaskName -properties "@{'Key'='Value'}"
####################################################

# External scripts
$CIRoot               = $PSScriptRoot
$PowerShellScriptPath = "$CIRoot\Scripts\"
$CIOutputDirectory    = "$CIRoot\..\CI-Output"

Include (Join-Path $PowerShellScriptPath "BuildHelperFunctions.ps1")
Include (Join-Path $PowerShellScriptPath "GeneralHelperFunctions.ps1")

# Define CLI input properties and defaults
properties {

   # Build
   $BuildConfiguration          = "release"
   $DotNetRunTime               = "win-x64"
   $DotNetFramework             = "net10.0"
   $msDeploy                    = "C:\Program Files\IIS\Microsoft Web Deploy V3\msdeploy.exe"    
   
   # Project paths
   $databaseProjectSourcePath   = "..\src\CryptoPriceNow.Data"
   $webProjectSourcePath        = "..\src\CryptoPriceNow.Web"
   $testProjectSourcePath       = "..\CryptoPriceNow.slnx"
   $compileSourcePath           = "..\src\CryptoPriceNow.Web\bin\output"

   $WebAppSettings              = "..\src\CryptoPriceNow.Web\appsettings.json"
   $DatabaseAppSettings         = "..\src\CryptoPriceNow.Data\appsettings.json"

   # Credentials
   $MsDeployLocation            = ""
   $webAppHost                  = ""
   $contentPathDes              = ""
   $msDeployUserName            = ""
   $msDeployPassword            = ""
   $dbConnectionString          = ""
   $NeutrinoApiUserId           = ""
   $NeutrinoApiApiKey           = ""
   $deploymentUserName          = ""
   $deploymentUserPassword      = ""
   $AzureSubscriptionId         = ""

   # Application Resources    
    $customDomain               = ""
    $requestProtocol            = "https://"
}

task default # required task

##############
# Compilation
##############
`
task -name BuildProject -description "Build Project"  -action { 
   
     exec {
     
        $webProjectPath = Resolve-Path -Path ("$CIRoot\$webProjectSourcePath")

        cd $webProjectPath

        dotnet restore
    }
}

task -name RestorePackages -description "Restore Packages"  -action { 
   
     exec {

        $compileSourcePath = ("$CIRoot\$compileSourcePath")
        
        if (Test-Path -Path $compileSourcePath){
            Write-Host "Deleting files at: '$compileSourcePath'... " -NoNewline
            
            Remove-Item $compileSourcePath -Force -Recurse

            Write-Host "done." -NoNewline
            Write-host
        }
                            
        $webProjectPath = Resolve-Path -Path ("$CIRoot\$webProjectSourcePath")

        cd $webProjectPath
        
        dotnet msbuild /t:Restore /p:Configuration=$BuildConfiguration
    }
}

task -name CreatePackage {

    exec {

        if (!(Test-Path -Path ("$CIRoot\$compileSourcePath")))
        {
            New-Item -Path ("$CIRoot\$compileSourcePath") -ItemType directory
        }

        $compileSourcePath = Resolve-Path -Path ("$CIRoot\$compileSourcePath")

        $webProjectPath = Resolve-Path -Path ("$CIRoot\$webProjectSourcePath")

        cd $webProjectPath

        Write-Host "Packaging..."
    
        & dotnet publish `
                    --framework $DotNetFramework `
                    --output $compileSourcePath `
                    --configuration $BuildConfiguration `
                    --runtime $DotNetRunTime `
                    --self-contained true
    }
}

task -name RunUnitTests {

    exec {
        $path = Resolve-Path -Path $testProjectSourcePath
        Write-Host "Test project: $path"
        dotnet test $path
    }
}

task -name SetConfigs {

   # $WebAppSettings = "..\src\cryptopricenow.Web\appsettings.json"
   # Set-FileSettings -fileLocation $WebAppSettings
   # Set-LoggingSettings($WebAppSettings)

   # $DatabaseAppSettings = "..\src\cryptopricenow.Data\appsettings.json"
   # Set-FileSettings -fileLocation $DatabaseAppSettings
}

############
# Deployment
############

task -name CustomServerDeploy `
     -depends DeployWebApp {
}

task -name SyncWebFiles {

    exec {

        $deployIisAppPath = $webAppHost
        $resolvedAppOfflineFilePath = Resolve-Path -Path ("$CIRoot\$AppOfflineFilePath")
        $compileSourcePath = Resolve-Path -Path ("$CIRoot\$compileSourcePath")

        Write-Host "-------------"
        Write-Host "Syncing files '$deployIisAppPath'..."

        & $msDeploy `
            -verb:sync `
            -source:IisApp=$compileSourcePath `
            -allowUntrusted:true `
            -dest:iisapp=$deployIisAppPath,computerName=$MsDeployLocation/MsDeploy.axd?site=$deployIisAppPath,authType='basic',username=$msDeployUserName,password=$msDeployPassword `
            -enableRule:AppOffline

        Write-Host "done."
    }
}

task -name MigrateDB -description "Runs migration of database"  -action { 
   
   #  exec {
   #   
   #    $databaseProjectSourcePath = Resolve-Path -Path ("$CIRoot\$databaseProjectSourcePath")
   #    cd $databaseProjectSourcePath
   #    Write-Host $databaseProjectSourcePath
   #
   #    dotnet ef database update --verbose
   # }
}

task -name DeployWebApp -depends SetConfigs, RestorePackages, BuildProject, RunUnitTests, CreatePackage, MigrateDB, SyncWebFiles -action {

    exec {

        $url = $requestProtocol + $webAppHost
        Write-Host "Deployment completed, requesting page '$url'..."    

        Retry-Command -ScriptBlock {
            $response = Invoke-WebRequest -Uri $url
 
            if ($response.StatusCode -eq 200)
            {
                Write-Host "done." -NoNewline
                Write-Host
                    
                Write-Host "COMPLETE!"
            }
            else
            {
				Write-Host "ERROR: "([int]$response.StatusCode)
            }
        } -Maximum 10
    }
}

##########################
# Other
##########################

task -name RenameFiles -description "Renames files in a directory" {

    $path = "c:\temp"
    $fileNamSubstringeToRemove = "-min"

    Get-ChildItem  -Path $path | Rename-Item -NewName { $_.Name -replace $fileNamSubstringeToRemove, '' }
}

##########################
# psake functions
##########################
FormatTaskName {
   param($taskName)
  
   write-host "----- Task: $taskName -----"   -foregroundcolor Cyan
}

TaskSetup {

    $time = Get-UtcDate
       
    Write-Host "Begin: $time "  -ForegroundColor DarkGray
}

TaskTearDown {

    Write-Host "----------"
}
 